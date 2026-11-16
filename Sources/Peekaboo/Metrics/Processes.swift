import Foundation
import Darwin
import AppKit

struct ProcInfo: Identifiable, Hashable {
    let pid: Int32
    let name: String
    let path: String
    let appPath: String?
    /// 1.0 = one full core, like Activity Monitor's "% CPU" / 100.
    var cpu: Double
    var memory: Int64
    var id: Int32 { pid }
}

struct AppGroup: Identifiable, Hashable {
    let id: String          // app bundle path, or "pid:<n>" for loose processes
    let name: String
    let appPath: String?
    var pids: [Int32]
    var cpu: Double
    var memory: Int64
    var isApp: Bool { appPath != nil }
}

final class ProcessReader {
    private struct Prev { var cpuTime: UInt64 }
    private var prev: [Int32: Prev] = [:]
    private var prevWall: UInt64 = 0
    private var nameCache: [String: String] = [:]
    private let timebase: (numer: UInt64, denom: UInt64) = {
        var tb = mach_timebase_info_data_t()
        mach_timebase_info(&tb)
        return (UInt64(tb.numer), UInt64(tb.denom))
    }()

    func read() -> [ProcInfo] {
        let count = proc_listallpids(nil, 0)
        guard count > 0 else { return [] }
        var pids = [Int32](repeating: 0, count: Int(count) + 32)
        let n = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<Int32>.size))
        guard n > 0 else { return [] }

        let wall = clock_gettime_nsec_np(CLOCK_MONOTONIC)
        let dtWall = prevWall > 0 ? Double(wall - prevWall) : 0
        var next: [Int32: Prev] = [:]
        var out: [ProcInfo] = []
        out.reserveCapacity(Int(n))

        var pathBuf = [CChar](repeating: 0, count: 4096)
        var nameBuf = [CChar](repeating: 0, count: 256)

        for pid in pids.prefix(Int(n)) where pid > 0 {
            var ri = rusage_info_v4()
            let rc = withUnsafeMutablePointer(to: &ri) {
                $0.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) {
                    proc_pid_rusage(pid, RUSAGE_INFO_V4, $0)
                }
            }
            guard rc == 0 else { continue }

            // ri_*_time is in mach absolute time units on Apple silicon.
            let cpuTicks = ri.ri_user_time + ri.ri_system_time
            let cpuNs = cpuTicks * timebase.numer / timebase.denom
            next[pid] = Prev(cpuTime: cpuNs)

            var cpu = 0.0
            if dtWall > 0, let p = prev[pid], cpuNs >= p.cpuTime {
                cpu = Double(cpuNs - p.cpuTime) / dtWall
            }

            let plen = proc_pidpath(pid, &pathBuf, UInt32(pathBuf.count))
            let path = plen > 0 ? String(cString: pathBuf) : ""
            var name: String
            if proc_name(pid, &nameBuf, UInt32(nameBuf.count)) > 0 {
                name = String(cString: nameBuf)
            } else {
                name = (path as NSString).lastPathComponent
            }
            if name.isEmpty { name = "pid \(pid)" }

            out.append(ProcInfo(pid: pid, name: name, path: path, appPath: Self.outerApp(path),
                                cpu: cpu, memory: Int64(ri.ri_phys_footprint)))
        }
        prev = next
        prevWall = wall
        return out
    }

    /// "/Applications/Lark.app/Contents/Frameworks/Helper.app/…" → "/Applications/Lark.app"
    static func outerApp(_ path: String) -> String? {
        guard let r = path.range(of: ".app/") else { return nil }
        return String(path[..<r.lowerBound]) + ".app"
    }

    func group(_ procs: [ProcInfo]) -> [AppGroup] {
        var groups: [String: AppGroup] = [:]
        for p in procs {
            if let app = p.appPath {
                if groups[app] == nil {
                    groups[app] = AppGroup(id: app, name: displayName(app), appPath: app, pids: [], cpu: 0, memory: 0)
                }
                groups[app]!.pids.append(p.pid)
                groups[app]!.cpu += p.cpu
                groups[app]!.memory += p.memory
            } else {
                let key = "pid:\(p.pid)"
                groups[key] = AppGroup(id: key, name: p.name, appPath: nil, pids: [p.pid], cpu: p.cpu, memory: p.memory)
            }
        }
        return Array(groups.values)
    }

    private func displayName(_ appPath: String) -> String {
        if let c = nameCache[appPath] { return c }
        var n = FileManager.default.displayName(atPath: appPath)
        if n.hasSuffix(".app") { n = String(n.dropLast(4)) }
        if let b = Bundle(path: appPath),
           let dn = (b.localizedInfoDictionary?["CFBundleDisplayName"] ?? b.infoDictionary?["CFBundleDisplayName"]) as? String,
           !dn.isEmpty {
            n = dn
        }
        nameCache[appPath] = n
        return n
    }
}

enum ProcessControl {
    /// Quits an app politely (like ⌘Q), or sends SIGTERM to a loose process.
    @discardableResult
    static func quit(_ g: AppGroup, force: Bool) -> Bool {
        if let path = g.appPath {
            let apps = NSWorkspace.shared.runningApplications.filter { $0.bundleURL?.path == path }
            if !apps.isEmpty {
                var ok = false
                for a in apps { ok = (force ? a.forceTerminate() : a.terminate()) || ok }
                return ok
            }
        }
        var ok = false
        for pid in g.pids { ok = (kill(pid, force ? SIGKILL : SIGTERM) == 0) || ok }
        return ok
    }

    static func icon(for g: AppGroup) -> NSImage {
        if let p = g.appPath { return NSWorkspace.shared.icon(forFile: p) }
        return NSWorkspace.shared.icon(for: .unixExecutable)
    }
}
