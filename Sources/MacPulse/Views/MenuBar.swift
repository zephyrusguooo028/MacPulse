import SwiftUI
import AppKit

enum MenuBarRenderer {
    static func diskText(_ b: Int64) -> String {
        let gb = Double(b) / 1_073_741_824
        return gb >= 100 ? String(format: "%.0fG", gb) : String(format: "%.1fG", gb)
    }
}

struct PopoverView: View {
    @Environment(AppState.self) private var app
    var style: PopoverStyle? = nil

    var body: some View {
        switch style ?? app.settings.popoverStyle {
        case .compact: CompactPopover()
        case .cards: CardsPopover()
        case .chart: ChartPopover()
        }
    }
}
