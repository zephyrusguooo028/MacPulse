import Foundation
import Darwin
import IOKit
import IOKit.ps

// MARK: - sysctl helpers

enum Sysctl {
    static func int(_ name: String) -> Int? {
        var v: Int64 = 0
        var size = MemoryLayout<Int64>.size
        guard sysctlbyname(name, &v, &size, nil, 0) == 0 else { return nil }
        if size == 4 { return Int(Int32(truncatingIfNeeded: v)) }
        return Int(v)
    }

    static func string(_ name: String) -> String? {
        var size = 0
        guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 0 else { return nil }
        var buf = [CChar](repeating: 0, count: size)
        guard sysctlbyname(name, &buf, &size, nil, 0) == 0 else { return nil }
        return String(cString: buf)
    }
}

// MARK: - CPU

struct CPUReading {
    var total: Double = 0       // 0…1 across all cores
    var user: Double = 0
    var system: Double = 0
    var perCore: [Double] = []
}

final class CPUReader {
    private var prev: [[UInt32]] = []
    /// Core tiers from fastest to slowest, e.g. [("Super", 2), ("Performance", 4), ("Efficiency", 6)].
    let levels: [(name: String, count: Int)]

    init() {
        let n = Sysctl.int("hw.nperflevels") ?? 1
        var l: [(String, Int)] = []
        for i in 0..<max(n, 1) {
            let count = Sysctl.int("hw.perflevel\(i).logicalcpu") ?? 0
            let name = Sysctl.string("hw.perflevel\(i).name") ?? (i == 0 ? "Performance" : "Efficiency")
            if count > 0 { l.append((name, count)) }
        }
        if l.isEmpty { l = [("Performance", Sysctl.int("hw.logicalcpu") ?? 1)] }
        levels = l
    }

    static func shortName(_ level: String) -> String {
        switch level.lowercased() {
        case "super": return L("超大核", "Super")
        case "performance": return L("性能核", "Performance")
        case "efficiency": return L("能效核", "Efficiency")
        default: return level
        }
    }

    /// One letter per logical CPU. Apple silicon numbers the slowest tier first.
    var coreLetters: [String] {
        levels.reversed().flatMap { Array(repeating: String($0.name.prefix(1)).uppercased(), count: $0.count) }
    }

    var summary: String {
        levels.map { "\($0.count) \(Self.shortName($0.name))" }.joined(separator: " · ")
    }

    func read() -> CPUReading {
        var numCPUs: natural_t = 0
        var info: processor_info_array_t?
        var count: mach_msg_type_number_t = 0
        guard host_processor_info(mach_host_self(), PROCESSOR_CPU_LOAD_INFO, &numCPUs, &info, &count) == KERN_SUCCESS,
              let info else { return CPUReading() }
        defer {
            vm_deallocate(mach_task_self_, vm_address_t(bitPattern: info), vm_size_t(Int(count) * MemoryLayout<integer_t>.stride))
        }

        var current: [[UInt32]] = []
        for i in 0..<Int(numCPUs) {
            let base = i * Int(CPU_STATE_MAX)
            current.append((0..<Int(CPU_STATE_MAX)).map { UInt32(bitPattern: info[base + $0]) })
        }
        defer { prev = current }
        guard prev.count == current.count else { return CPUReading(perCore: Array(repeating: 0, count: current.count)) }

        var r = CPUReading()
        var tu: Double = 0, ts: Double = 0, tAll: Double = 0
        for (c, p) in zip(current, prev) {
            let user = Double(c[Int(CPU_STATE_USER)] &- p[Int(CPU_STATE_USER)])
            let sys = Double(c[Int(CPU_STATE_SYSTEM)] &- p[Int(CPU_STATE_SYSTEM)])
            let idle = Double(c[Int(CPU_STATE_IDLE)] &- p[Int(CPU_STATE_IDLE)])
            let nice = Double(c[Int(CPU_STATE_NICE)] &- p[Int(CPU_STATE_NICE)])
            let all = user + sys + idle + nice
            r.perCore.append(all > 0 ? (user + sys + nice) / all : 0)
            tu += user + nice; ts += sys; tAll += all
        }
        if tAll > 0 {
            r.user = tu / tAll
            r.system = ts / tAll
            r.total = r.user + r.system
        }
        return r
    }
}

// MARK: - Memory

struct MemoryReading {
    var total: UInt64 = 0
    var used: UInt64 = 0
    var app: UInt64 = 0
    var wired: UInt64 = 0
    var compressed: UInt64 = 0
    var cached: UInt64 = 0
    var swapUsed: UInt64 = 0
    var swapTotal: UInt64 = 0
    /// 1 normal, 2 warning, 4 critical
    var pressureLevel: Int = 1

    var usedFraction: Double { total > 0 ? Double(used) / Double(total) : 0 }
    var pressureText: String {
        switch pressureLevel {
        case 4: return L("紧张", "Critical")
        case 2: return L("偏高", "Elevated")
        default: return L("正常", "Normal")
        }
    }
}

enum MemoryReader {
    static let total: UInt64 = UInt64(Sysctl.int("hw.memsize") ?? 0)

    static func read() -> MemoryReading {
        var r = MemoryReading()
        r.total = total
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.stride / MemoryLayout<integer_t>.stride)
        let kr = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        if kr == KERN_SUCCESS {
            let page = UInt64(vm_kernel_page_size)
            let internalPages = UInt64(stats.internal_page_count)
            let purgeable = UInt64(stats.purgeable_count)
            r.app = (internalPages > purgeable ? internalPages - purgeable : 0) * page
            r.wired = UInt64(stats.wire_count) * page
            r.compressed = UInt64(stats.compressor_page_count) * page
            r.cached = (UInt64(stats.external_page_count) + purgeable) * page
            r.used = min(r.app + r.wired + r.compressed, r.total)
        }

        var swap = xsw_usage()
        var size = MemoryLayout<xsw_usage>.size
        if sysctlbyname("vm.swapusage", &swap, &size, nil, 0) == 0 {
            r.swapUsed = swap.xsu_used
            r.swapTotal = swap.xsu_total
        }
        r.pressureLevel = Sysctl.int("kern.memorystatus_vm_pressure_level") ?? 1
        return r
    }
}

// MARK: - Disk

struct DiskReading {
    var total: Int64 = 0
    /// What Finder calls "available": includes space macOS can purge on demand.
    var available: Int64 = 0
    /// Truly free right now.
    var free: Int64 = 0
    var used: Int64 { max(total - available, 0) }
    var purgeable: Int64 { max(available - free, 0) }
    var usedFraction: Double { total > 0 ? Double(used) / Double(total) : 0 }
}

enum DiskReader {
    static func read(path: String = "/") -> DiskReading {
        let url = URL(fileURLWithPath: path)
        let keys: Set<URLResourceKey> = [.volumeTotalCapacityKey, .volumeAvailableCapacityForImportantUsageKey, .volumeAvailableCapacityKey]
        guard let v = try? url.resourceValues(forKeys: keys) else { return DiskReading() }
        var r = DiskReading()
        r.total = Int64(v.volumeTotalCapacity ?? 0)
        r.free = Int64(v.volumeAvailableCapacity ?? 0)
        r.available = v.volumeAvailableCapacityForImportantUsage ?? r.free
        return r
    }
}

// MARK: - Network

final class NetworkReader {
    private var prevIn: UInt64 = 0
    private var prevOut: UInt64 = 0
    private var prevTime: TimeInterval = 0

    /// Returns bytes/second (in, out) since the previous call.
    func read() -> (inRate: Double, outRate: Double, totalIn: UInt64, totalOut: UInt64) {
        let (i, o) = Self.counters()
        let now = ProcessInfo.processInfo.systemUptime
        defer { prevIn = i; prevOut = o; prevTime = now }
        guard prevTime > 0, now > prevTime, i >= prevIn, o >= prevOut else { return (0, 0, i, o) }
        let dt = now - prevTime
        return (Double(i - prevIn) / dt, Double(o - prevOut) / dt, i, o)
    }

    static func counters() -> (UInt64, UInt64) {
        var mib: [Int32] = [CTL_NET, PF_ROUTE, 0, 0, NET_RT_IFLIST2, 0]
        var len = 0
        guard sysctl(&mib, 6, nil, &len, nil, 0) == 0, len > 0 else { return (0, 0) }
        let buf = UnsafeMutableRawPointer.allocate(byteCount: len, alignment: 8)
        defer { buf.deallocate() }
        guard sysctl(&mib, 6, buf, &len, nil, 0) == 0 else { return (0, 0) }

        var totalIn: UInt64 = 0, totalOut: UInt64 = 0
        var offset = 0
        var nameBuf = [CChar](repeating: 0, count: Int(IF_NAMESIZE) + 1)
        while offset + MemoryLayout<if_msghdr>.size <= len {
            let p = buf.advanced(by: offset)
            let hdr = p.loadUnaligned(as: if_msghdr.self)
            if hdr.ifm_msglen == 0 { break }
            if Int32(hdr.ifm_type) == RTM_IFINFO2, offset + MemoryLayout<if_msghdr2>.size <= len {
                let m2 = p.loadUnaligned(as: if_msghdr2.self)
                if if_indextoname(UInt32(m2.ifm_index), &nameBuf) != nil {
                    let name = String(cString: nameBuf)
                    // Physical interfaces only; tunnels (utun) would double-count VPN traffic.
                    if name.hasPrefix("en") || name.hasPrefix("pdp_ip") {
                        totalIn &+= m2.ifm_data.ifi_ibytes
                        totalOut &+= m2.ifm_data.ifi_obytes
                    }
                }
            }
            offset += Int(hdr.ifm_msglen)
        }
        return (totalIn, totalOut)
    }
}

// MARK: - Battery

struct BatteryReading {
    var percent: Double
    var charging: Bool
    var onAC: Bool
    var minutesLeft: Int?
}

enum BatteryReader {
    static func read() -> BatteryReading? {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let list = IOPSCopyPowerSourcesList(info)?.takeRetainedValue() as? [CFTypeRef] else { return nil }
        for ps in list {
            guard let d = IOPSGetPowerSourceDescription(info, ps)?.takeUnretainedValue() as? [String: Any],
                  (d[kIOPSTypeKey] as? String) == kIOPSInternalBatteryType else { continue }
            let cur = d[kIOPSCurrentCapacityKey] as? Int ?? 0
            let max = d[kIOPSMaxCapacityKey] as? Int ?? 100
            let t = d[kIOPSTimeToEmptyKey] as? Int
            return BatteryReading(
                percent: max > 0 ? Double(cur) / Double(max) : 0,
                charging: d[kIOPSIsChargingKey] as? Bool ?? false,
                onAC: (d[kIOPSPowerSourceStateKey] as? String) == kIOPSACPowerValue,
                minutesLeft: (t ?? -1) > 0 ? t : nil
            )
        }
        return nil
    }
}

// MARK: - GPU

enum GPUReader {
    /// Device utilisation 0…1, read from the IOAccelerator performance statistics.
    static func read() -> Double? {
        var iter: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOAccelerator"), &iter) == KERN_SUCCESS else { return nil }
        defer { IOObjectRelease(iter) }
        var best: Double?
        var entry = IOIteratorNext(iter)
        while entry != 0 {
            var props: Unmanaged<CFMutableDictionary>?
            if IORegistryEntryCreateCFProperties(entry, &props, kCFAllocatorDefault, 0) == KERN_SUCCESS,
               let dict = props?.takeRetainedValue() as? [String: Any],
               let perf = dict["PerformanceStatistics"] as? [String: Any],
               let util = perf["Device Utilization %"] as? Int {
                best = max(best ?? 0, Double(util) / 100)
            }
            IOObjectRelease(entry)
            entry = IOIteratorNext(iter)
        }
        return best
    }
}

// MARK: - Temperature (best effort, private IOHID API)

/// Apple silicon exposes die temperatures through the HID event system. The API is
/// private, so everything is resolved at runtime and the reader simply returns nil
/// if a symbol is missing.
final class TemperatureReader {
    private typealias CreateFn = @convention(c) (CFAllocator?) -> Unmanaged<AnyObject>?
    private typealias SetMatchingFn = @convention(c) (AnyObject, CFDictionary) -> Int32
    private typealias CopyServicesFn = @convention(c) (AnyObject) -> Unmanaged<CFArray>?
    private typealias CopyPropertyFn = @convention(c) (AnyObject, CFString) -> Unmanaged<CFTypeRef>?
    private typealias CopyEventFn = @convention(c) (AnyObject, Int64, Int32, Int64) -> Unmanaged<AnyObject>?
    private typealias GetFloatFn = @convention(c) (AnyObject, Int32) -> Double

    private var client: AnyObject?
    private var copyServices: CopyServicesFn?
    private var copyProperty: CopyPropertyFn?
    private var copyEvent: CopyEventFn?
    private var getFloat: GetFloatFn?

    init() {
        guard let h = dlopen("/System/Library/Frameworks/IOKit.framework/IOKit", RTLD_NOW) else { return }
        func sym<T>(_ n: String, _ t: T.Type) -> T? {
            guard let p = dlsym(h, n) else { return nil }
            return unsafeBitCast(p, to: t)
        }
        guard let create = sym("IOHIDEventSystemClientCreate", CreateFn.self),
              let setMatching = sym("IOHIDEventSystemClientSetMatching", SetMatchingFn.self) else { return }
        copyServices = sym("IOHIDEventSystemClientCopyServices", CopyServicesFn.self)
        copyProperty = sym("IOHIDServiceClientCopyProperty", CopyPropertyFn.self)
        copyEvent = sym("IOHIDServiceClientCopyEvent", CopyEventFn.self)
        getFloat = sym("IOHIDEventGetFloatValue", GetFloatFn.self)
        guard let c = create(kCFAllocatorDefault)?.takeRetainedValue() else { return }
        let match: [String: Int] = ["PrimaryUsagePage": 0xff00, "PrimaryUsage": 5]
        _ = setMatching(c, match as CFDictionary)
        client = c
    }

    /// Sensors worth reading, found once: die sensors if there are any, otherwise all.
    private var sensors: [AnyObject]?

    private func findSensors() -> [AnyObject] {
        guard let client, let copyServices, let copyProperty,
              let services = copyServices(client)?.takeRetainedValue() as? [AnyObject] else { return [] }
        let die = services.filter { s in
            let name = (copyProperty(s, "Product" as CFString)?.takeRetainedValue() as? String) ?? ""
            return name.localizedCaseInsensitiveContains("tdie") || name.localizedCaseInsensitiveContains("tdev") || name.contains("ACC")
        }
        // Each read is an IPC round trip, so a handful of die sensors is plenty.
        return Array((die.isEmpty ? services : die).prefix(6))
    }

    /// Hottest CPU die sensor in °C.
    func read() -> Double? {
        guard let copyEvent, let getFloat else { return nil }
        if sensors == nil { sensors = findSensors() }
        let kTemperatureEvent: Int64 = 15
        let field = Int32(15 << 16)
        var best: Double?
        for s in sensors ?? [] {
            guard let ev = copyEvent(s, kTemperatureEvent, 0, 0)?.takeRetainedValue() else { continue }
            let t = getFloat(ev, field)
            if t > 5, t < 130 { best = max(best ?? t, t) }
        }
        return best
    }
}

// MARK: - Misc

enum SystemInfo {
    static var model: String { Sysctl.string("hw.model") ?? "Mac" }
    static var chip: String { Sysctl.string("machdep.cpu.brand_string") ?? "" }
    static var osVersion: String {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        return "macOS \(v.majorVersion).\(v.minorVersion)"
    }
    static var uptime: TimeInterval {
        var tv = timeval()
        var size = MemoryLayout<timeval>.size
        guard sysctlbyname("kern.boottime", &tv, &size, nil, 0) == 0 else { return 0 }
        return Date().timeIntervalSince1970 - Double(tv.tv_sec)
    }
    static var loadAverage: [Double] {
        var l = [Double](repeating: 0, count: 3)
        return getloadavg(&l, 3) == 3 ? l : []
    }
    static func uptimeText() -> String {
        let s = Int(uptime)
        let d = s / 86400, h = (s % 86400) / 3600, m = (s % 3600) / 60
        if d > 0 { return L("\(d) 天 \(h) 小时", "\(d)d \(h)h") }
        if h > 0 { return L("\(h) 小时 \(m) 分", "\(h)h \(m)m") }
        return L("\(m) 分钟", "\(m) min")
    }
}
