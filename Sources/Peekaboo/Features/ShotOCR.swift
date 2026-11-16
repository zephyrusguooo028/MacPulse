import AppKit
import SwiftUI
import Vision

/// Text recognition for the screenshot toolbar's "OCR" button. Runs on-device with
/// Vision, shows the result in a small floating window and copies it right away.
enum ShotOCR {
    @MainActor private static var windows: [NSWindow] = []

    @MainActor
    static func run(image: NSImage) {
        let job = OCRJob()
        let host = NSHostingController(rootView: OCRResultView(job: job))
        let w = NSWindow(contentViewController: host)
        w.title = L("识别到的文字", "Recognized text")
        w.styleMask = [.titled, .closable, .resizable]
        w.level = .floating
        w.isReleasedWhenClosed = false
        w.setContentSize(NSSize(width: 480, height: 360))
        w.center()
        job.close = { [weak w] in w?.close() }
        windows.append(w)
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { n in
            MainActor.assumeIsolated { windows.removeAll { $0 === n.object as AnyObject } }
        }
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)

        guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { job.finish(nil); return }
        DispatchQueue.global(qos: .userInitiated).async {
            let text = recognize(cg)
            DispatchQueue.main.async { MainActor.assumeIsolated { job.finish(text) } }
        }
    }

    /// Returns the recognized lines joined by newlines, or nil on failure.
    nonisolated static func recognize(_ cg: CGImage) -> String? {
        let req = VNRecognizeTextRequest()
        req.recognitionLevel = .accurate
        req.recognitionLanguages = ["zh-Hans", "zh-Hant", "en-US"]
        req.usesLanguageCorrection = true
        do {
            try VNImageRequestHandler(cgImage: cg, options: [:]).perform([req])
        } catch {
            return nil
        }
        // Vision returns observations in reading order (top to bottom).
        let obs = req.results ?? []
        return obs.compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
    }
}

@MainActor
@Observable
final class OCRJob {
    enum State { case working, done, empty, failed }
    var state = State.working
    var text = ""
    var copied = false
    @ObservationIgnored var close: () -> Void = {}

    func finish(_ result: String?) {
        guard let result else { state = .failed; return }
        let t = result.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty { state = .empty; return }
        text = t
        state = .done
        copy()
    }

    func copy() {
        NSPasteboard.general.clearContents()
        copied = NSPasteboard.general.setString(text, forType: .string)
    }
}

private struct OCRResultView: View {
    let job: OCRJob

    var body: some View {
        @Bindable var j = job
        VStack(alignment: .leading, spacing: 10) {
            switch job.state {
            case .working:
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(L("正在识别文字…", "Recognizing text…")).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            case .empty, .failed:
                EmptyHint(symbol: "text.viewfinder",
                          title: job.state == .empty ? L("没有找到文字", "No text found") : L("识别失败", "Recognition failed"),
                          detail: L("试试框选更大、更清晰的文字区域。支持简体中文、繁体中文和英文。", "Try selecting a larger, clearer area of text. Simplified Chinese, Traditional Chinese and English are supported."))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            case .done:
                TextEditor(text: $j.text)
                    .font(.system(size: 13))
                    .scrollContentBackground(.hidden)
                    .padding(6)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color(nsColor: .textBackgroundColor)))
                    .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Color.primary.opacity(0.1)))
                    .onChange(of: job.text) { _, _ in job.copied = false }
            }
            HStack {
                if job.state == .done {
                    Image(systemName: job.copied ? "checkmark.circle.fill" : "info.circle")
                        .foregroundStyle(job.copied ? Palette.disk : Color.secondary)
                    Text(job.copied ? L("已复制到剪贴板", "Copied to the clipboard") : L("改过的文字点「复制」再拷贝一次", "Edited? Click Copy to copy it again"))
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button(L("复制", "Copy")) { job.copy() }
                    .disabled(job.state != .done || job.text.isEmpty)
                    .keyboardShortcut("c", modifiers: [.command, .shift])
                Button(L("关闭", "Close")) { job.close() }
                    .keyboardShortcut(.cancelAction)
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(14)
        .frame(minWidth: 380, minHeight: 240)
    }
}
