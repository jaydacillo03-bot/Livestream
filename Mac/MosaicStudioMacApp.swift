import SwiftUI

@main
struct MosaicStudioMacApp: App {
    @StateObject private var studio = StudioViewModel()

    var body: some Scene {
        WindowGroup {
            StudioView()
                .environmentObject(studio)
                .frame(minWidth: 1000, minHeight: 680)
                .preferredColorScheme(.dark)
        }
        .windowStyle(.titleBar)
    }
}
