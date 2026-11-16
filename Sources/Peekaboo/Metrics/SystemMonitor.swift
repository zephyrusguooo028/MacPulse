import Foundation
import Observation

struct LiveSample: Identifiable {
    let t: Date
    let cpu: Double
    let mem: Double
    let netIn: Double
    let netOut: Double
    var id: Date { t }
}

struct MinutePoint: Codable, Identifiable {
    let t: Date
    let cpu: Double
    let cpuMax: Double
    let mem: Double
    let netIn: Double   // average bytes/s over the minute
    let netOut: Double
    let temp: Double?
    var id: Date { t }
}

/// Keeps one point per minute for the last 24 hours, persisted to disk.
final class HistoryStore {
    private(set) var points: [MinutePoint] = []
    private var bucketStart: Date?
    private var sums = (cpu: 0.0, cpuMax: 0.0, mem: 0.0, netIn: 0.0, netOut: 0.0, temp: 0.0, tempN: 0, n: 0)
    private var lastSave = Date()
    private let url = Paths.support.appendingPathComponent("history.json")

    init() {
        if let data = try? Data(contentsOf: url),
           let p = try? JSONDecoder().decode([MinutePoint].self, from: data) {
            points = p.filter { $0.t > Date().addingTimeInterval(-86400) }
        }
    }

    /// Returns true when a new minute point was appended.
    @discardableResult
    func add(cpu: Double, mem: Double, netIn: Double, netOut: Double, temp: Double?, at now: Date = Date()) -> Bool {
        let minute = Date(timeIntervalSince1970: floor(now.timeIntervalSince1970 / 60) * 60)
        var appended = false
        if let start = bucketStart, start != minute, sums.n > 0 {
            let n = Double(sums.n)
            points.append(MinutePoint(t: start, cpu: sums.cpu / n, cpuMax: sums.cpuMax, mem: sums.mem / n,
                                      netIn: sums.netIn / n, netOut: sums.netOut / n,
                                      temp: sums.tempN > 0 ? sums.temp / Double(sums.tempN) : nil))
            let cutoff = now.addingTimeInterval(-86400)
            if let first = points.first, first.t < cutoff { points.removeAll { $0.t < cutoff } }
            sums = (0, 0, 0, 0, 0, 0, 0, 0)
            appended = true
        }
        bucketStart = minute
        sums.cpu += cpu; sums.cpuMax = max(sums.cpuMax, cpu); sums.mem += mem
        sums.netIn += netIn; sums.netOut += netOut; sums.n += 1
        if let temp { sums.temp += temp; sums.tempN += 1 }
        if now.timeIntervalSince(lastSave) > 300 { save() }
        return appended
    }

    func save() {
        lastSave = Date()
        if let data = try? JSONEncoder().encode(points) { try? data.write(to: url, options: .atomic) }
    }
}

@MainActor
@Observable
final class SystemMonitor {
    static let shared = SystemMonitor()

    var cpu = CPUReading()
    var memory = MemoryReading()
    var disk = DiskReading()
    var netIn: Double = 0
    var netOut: Double = 0
    var battery: BatteryReading?
    var temperature: Double?
    var gpu: Double?
    var samples: [LiveSample] = []
    var history: [MinutePoint] = []

    var processes: [ProcInfo] = []
    var groups: [AppGroup] = []
    var topByCPU: [AppGroup] { groups.sorted { $0.cpu > $1.cpu }.prefix(5).map { $0 } }

    let coreLetters: [String]
    let coreSummary: String

    @ObservationIgnored private let cpuReader = CPUReader()
    @ObservationIgnored private let netReader = NetworkReader()
    @ObservationIgnored private let procReader = ProcessReader()
    @ObservationIgnored private let tempReader = TemperatureReader()
    @ObservationIgnored private let store = HistoryStore()
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var tick = 0
    @ObservationIgnored private var processDemand = 0
    @ObservationIgnored var onTick: ((SystemMonitor) -> Void)?

    private init() {
        coreLetters = cpuReader.coreLetters
        coreSummary = cpuReader.summary
        history = store.points
    }

    func start() {
        _ = cpuReader.read()
        _ = netReader.read()
        sample()
        reschedule()
    }

    func reschedule() {
        timer?.invalidate()
        let t = Timer(timeInterval: max(AppSettings.shared.sampleInterval, 1), repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sample() }
        }
        t.tolerance = 0.2
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    /// Process sampling is the costliest read, so it only runs while a view shows processes.
    func wantProcesses(_ on: Bool) {
        processDemand = max(0, processDemand + (on ? 1 : -1))
        if on { refreshProcesses() }
    }

    func refreshProcesses() {
        let p = procReader.read()
        processes = p
        groups = procReader.group(p)
    }

    func sample() {
        tick += 1
        cpu = cpuReader.read()
        memory = MemoryReader.read()
        let n = netReader.read()
        netIn = n.inRate; netOut = n.outRate
        // Slow-changing readings are refreshed less often to keep Peekaboo light.
        if tick % 10 == 1 {
            disk = DiskReader.read()
            battery = BatteryReader.read()
        }
        if tick % 2 == 1 { gpu = GPUReader.read() }
        if !AppSettings.shared.showTemperature {
            temperature = nil
        } else if tick % 5 == 1 {
            temperature = tempReader.read()
        }
        if processDemand > 0 { refreshProcesses() }

        samples.append(LiveSample(t: Date(), cpu: cpu.total, mem: memory.usedFraction, netIn: netIn, netOut: netOut))
        if samples.count > 150 { samples.removeFirst(samples.count - 150) }
        if store.add(cpu: cpu.total, mem: memory.usedFraction, netIn: netIn, netOut: netOut, temp: temperature) {
            history = store.points
        }
        onTick?(self)
    }

    func shutdown() { store.save() }
}
