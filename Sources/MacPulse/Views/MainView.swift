import SwiftUI

struct MainView: View {
    @Environment(AppState.self) private var app

    var body: some View {
        @Bindable var app = app
        NavigationSplitView {
            List(selection: Binding(get: { app.tab }, set: { if let t = $0 { app.tab = t } })) {
                Section {
                    ForEach(MainTab.allCases.filter { $0 != .settings }) { t in
                        Label(t.title, systemImage: t.symbol).tag(t)
                    }
                }
                Section {
                    Label(MainTab.settings.title, systemImage: MainTab.settings.symbol).tag(MainTab.settings)
                }
            }
            .navigationSplitViewColumnWidth(min: 170, ideal: 190, max: 230)
            .safeAreaInset(edge: .bottom) { SidebarFooter().padding(12) }
        } detail: {
            Group {
                switch app.tab {
                case .overview: OverviewView()
                case .disk: DiskView()
                case .projects: ProjectsView()
                case .processes: ProcessesView()
                case .cleanup: CleanupView()
                case .settings: SettingsView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(Color(nsColor: .windowBackgroundColor))
        }
        .tint(Palette.theme)
    }
}

private struct SidebarFooter: View {
    @Environment(AppState.self) private var app
    var body: some View {
        let d = app.monitor.disk
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Image(systemName: "internaldrive").foregroundStyle(.secondary)
                Text("Macintosh HD").font(.caption.weight(.medium))
            }
            SizeBar(fraction: d.usedFraction, color: d.usedFraction > 0.9 ? Palette.danger : Palette.disk)
            Text("可用 \(Fmt.bytes(d.available)) / 共 \(Fmt.bytes(d.total))")
                .font(.caption2).foregroundStyle(.secondary).monospacedDigit()
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.04)))
    }
}
