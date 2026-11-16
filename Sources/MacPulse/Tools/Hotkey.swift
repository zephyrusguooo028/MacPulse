import Carbon
import AppKit

/// Global hotkeys through Carbon's RegisterEventHotKey: works without any
/// accessibility or input-monitoring permission.
@MainActor
final class Hotkeys {
    static let shared = Hotkeys()
    private var refs: [UInt32: EventHotKeyRef] = [:]
    private var actions: [UInt32: () -> Void] = [:]
    private var installed = false

    func register(id: UInt32, keyCode: UInt32, modifiers: UInt32, action: @escaping () -> Void) {
        installHandler()
        unregister(id: id)
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: OSType(0x4D50_4C53), id: id) // "MPLS"
        if RegisterEventHotKey(keyCode, modifiers, hkID, GetApplicationEventTarget(), 0, &ref) == noErr, let ref {
            refs[id] = ref
            actions[id] = action
        }
    }

    func unregister(id: UInt32) {
        if let r = refs.removeValue(forKey: id) { UnregisterEventHotKey(r) }
        actions[id] = nil
    }

    fileprivate func fire(_ id: UInt32) { actions[id]?() }

    private func installHandler() {
        guard !installed else { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hk = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
                              MemoryLayout<EventHotKeyID>.size, nil, &hk)
            let id = hk.id
            DispatchQueue.main.async { MainActor.assumeIsolated { Hotkeys.shared.fire(id) } }
            return noErr
        }, 1, &spec, nil, nil)
    }
}

enum ShotHotkey: String, CaseIterable, Identifiable {
    case optShiftA, ctrlOptA, cmdShift2, none
    var id: String { rawValue }
    var title: String {
        switch self {
        case .optShiftA: return "⌥⇧A"
        case .ctrlOptA: return "⌃⌥A"
        case .cmdShift2: return "⌘⇧2"
        case .none: return "不使用快捷键"
        }
    }
    var key: (code: UInt32, mods: UInt32)? {
        switch self {
        case .optShiftA: return (UInt32(kVK_ANSI_A), UInt32(optionKey | shiftKey))
        case .ctrlOptA: return (UInt32(kVK_ANSI_A), UInt32(controlKey | optionKey))
        case .cmdShift2: return (UInt32(kVK_ANSI_2), UInt32(cmdKey | shiftKey))
        case .none: return nil
        }
    }
}
