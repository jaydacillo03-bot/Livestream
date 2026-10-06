import AVFoundation
import SwiftUI

struct CapturePreview: NSViewRepresentable {
    let session: AVCaptureSession

    func makeNSView(context: Context) -> CameraPreviewNSView {
        let view = CameraPreviewNSView()
        view.previewLayer.session = session
        return view
    }

    func updateNSView(_ view: CameraPreviewNSView, context: Context) {
        if view.previewLayer.session !== session {
            view.previewLayer.session = session
        }
    }
}

final class CameraPreviewNSView: NSView {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer = previewLayer
        previewLayer.videoGravity = .resizeAspect
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    let previewLayer = AVCaptureVideoPreviewLayer()

    override func layout() {
        super.layout()
        previewLayer.frame = bounds
    }
}
