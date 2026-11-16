// Scaffolding for v1.1 features. Each feature owner replaces its placeholder by
// deleting it here and adding a real implementation in its own file(s).
import SwiftUI
import AppKit

// MARK: Clipboard history + OCR (owner: clipboard)
enum ClipboardService { @MainActor static func startIfEnabled() {} }
struct ClipboardView: View { static var badge: String? { nil }; var body: some View { Text("Clipboard") } }
struct ClipboardSettingsCard: View { var body: some View { EmptyView() } }
enum ShotOCR { @MainActor static func run(image: NSImage) {} }
