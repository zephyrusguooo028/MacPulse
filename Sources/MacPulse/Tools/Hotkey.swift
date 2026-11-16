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

    @discardableResult
    func register(id: UInt32, keyCode: UInt32, modifiers: UInt32, action: @escaping () -> Void) -> Bool {
        installHandler()
        unregister(id: id)
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: OSType(0x4D50_4C53), id: id) // "MPLS"
        if RegisterEventHotKey(keyCode, modifiers, hkID, GetApplicationEventTarget(), 0, &ref) == noErr, let ref {
            refs[id] = ref
            actions[id] = action
            return true
        }
        return false
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

/// A user-recorded shortcut: Carbon key code + Carbon modifier mask.
struct HotkeySpec: Codable, Equatable {
    var keyCode: UInt32
    var modifiers: UInt32
    var label: String

    static let defaultShot = HotkeySpec(keyCode: UInt32(kVK_ANSI_A), modifiers: UInt32(optionKey | shiftKey), label: "A")

    var title: String {
        var s = ""
        if modifiers & UInt32(controlKey) != 0 { s += "⌃" }
        if modifiers & UInt32(optionKey) != 0 { s += "⌥" }
        if modifiers & UInt32(shiftKey) != 0 { s += "⇧" }
        if modifiers & UInt32(cmdKey) != 0 { s += "⌘" }
        return s + label
    }

    static let functionKeys: [UInt16: String] = [
        UInt16(kVK_F1): "F1", UInt16(kVK_F2): "F2", UInt16(kVK_F3): "F3", UInt16(kVK_F4): "F4", UInt16(kVK_F5): "F5", UInt16(kVK_F6): "F6",
        UInt16(kVK_F7): "F7", UInt16(kVK_F8): "F8", UInt16(kVK_F9): "F9", UInt16(kVK_F10): "F10", UInt16(kVK_F11): "F11", UInt16(kVK_F12): "F12",
    ]
    static let namedKeys: [UInt16: String] = [
        UInt16(kVK_Space): "空格", UInt16(kVK_Return): "↩", UInt16(kVK_Tab): "⇥", UInt16(kVK_LeftArrow): "←", UInt16(kVK_RightArrow): "→",
        UInt16(kVK_UpArrow): "↑", UInt16(kVK_DownArrow): "↓", UInt16(kVK_Delete): "⌫",
    ]

    /// Builds a spec from a key event. Returns nil when it needs a modifier and has none.
    static func from(_ e: NSEvent) -> HotkeySpec? {
        var m: UInt32 = 0
        let f = e.modifierFlags
        if f.contains(.control) { m |= UInt32(controlKey) }
        if f.contains(.option) { m |= UInt32(optionKey) }
        if f.contains(.shift) { m |= UInt32(shiftKey) }
        if f.contains(.command) { m |= UInt32(cmdKey) }
        let isF = functionKeys[e.keyCode] != nil
        // Plain letters would fire while typing, so require ⌃, ⌥ or ⌘ unless it is an F-key.
        guard isF || m & UInt32(controlKey | optionKey | cmdKey) != 0 else { return nil }
        let label = functionKeys[e.keyCode] ?? namedKeys[e.keyCode] ?? (e.charactersIgnoringModifiers ?? "?").uppercased()
        return HotkeySpec(keyCode: UInt32(e.keyCode), modifiers: m, label: label)
    }
}
