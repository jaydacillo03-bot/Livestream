import AVKit
import SwiftUI

struct StudioView: View {
    @EnvironmentObject private var studio: StudioViewModel
    @State private var workspace = Workspace.studio
    @State private var editingDestination: EditableDestination?

    private enum Workspace: String, CaseIterable {
        case studio = "Studio"
        case watch = "Watch"
    }

    var body: some View {
        HStack(spacing: 0) {
            sidebar
                .frame(width: 290)
            Divider()
            mainContent
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            if workspace == .studio {
                Divider()
                broadcastSidebar
                    .frame(width: 300)
            }
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .toolbar {
            ToolbarItem(placement: .principal) {
                Picker("Workspace", selection: $workspace) {
                    ForEach(Workspace.allCases, id: \.self) { item in
                        Text(item.rawValue).tag(item)
                    }
                }
                .pickerStyle(.segmented)
                .frame(width: 200)
            }
        }
        .task {
            await studio.refreshDevices()
            if let selected = studio.selectedFeed {
                studio.selectFeed(selected)
            }
        }
        .sheet(item: $editingDestination) { item in
            DestinationEditor(destination: item.destination) { name, url, key, id in
                try studio.saveDestination(name: name, serverURL: url, streamKey: key, id: id)
            }
        }
        .onDisappear {
            studio.shutdown()
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 15) {
            HStack(spacing: 10) {
                Image(systemName: "dot.radiowaves.left.and.right")
                    .font(.title2.weight(.semibold))
                    .foregroundStyle(.purple)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Mosaic Studio").font(.headline)
                    Text(workspace == .watch ? "AUDIENCE PREVIEW" : "LIVE PRODUCTION")
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(.secondary)
                }
            }

            HStack {
                Text("FEEDS").font(.caption.weight(.bold)).foregroundStyle(.secondary)
                Spacer()
                Button {
                    Task { await studio.refreshDevices() }
                } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
                .controlSize(.small)
                .disabled(studio.isBusy)
            }

            ScrollView {
                VStack(spacing: 7) {
                    ForEach(studio.feeds) { feed in
                        feedRow(feed)
                    }
                }
            }
            .frame(maxHeight: .infinity)

            if workspace == .studio {
                Picker("Camera", selection: $studio.selectedCameraID) {
                    if studio.cameras.isEmpty {
                        Text("No cameras found").tag("")
                    }
                    ForEach(studio.cameras) { camera in
                        Text(camera.name).tag(camera.id)
                    }
                }
                .labelsHidden()
                .disabled(studio.cameras.isEmpty || studio.isBusy)

                HStack(spacing: 8) {
                    Button {
                        studio.addSelectedCamera()
                    } label: {
                        Label("Add Camera", systemImage: "camera")
                    }
                    .disabled(studio.selectedCameraID.isEmpty || studio.isBusy)

                    Button {
                        studio.importVideo()
                    } label: {
                        Label("Import Video", systemImage: "film")
                    }
                    .disabled(studio.isBusy)
                }
                .controlSize(.small)

                if studio.selectedFeed != nil {
                    Button(role: .destructive) {
                        studio.removeSelectedFeed()
                    } label: {
                        Label("Remove Selected Feed", systemImage: "trash")
                    }
                    .controlSize(.small)
                    .disabled(studio.isBusy)
                }
            }
        }
        .padding(16)
    }

    private func feedRow(_ feed: StudioFeed) -> some View {
        let isSelected = studio.selectedFeedID == feed.id
        return Button {
            studio.selectFeed(feed)
        } label: {
            HStack(spacing: 10) {
                Image(systemName: feed.kind == .camera ? "video" : "film")
                    .frame(width: 25, height: 25)
                    .background(.purple.opacity(0.2), in: RoundedRectangle(cornerRadius: 7))
                VStack(alignment: .leading, spacing: 2) {
                    Text(feed.title).font(.callout.weight(.medium)).lineLimit(1)
                    Text(feed.kind == .camera ? "CAMERA FEED" : "VIDEO FEED")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                if isSelected {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.purple)
                }
            }
            .padding(9)
            .background(isSelected ? Color.purple.opacity(0.16) : Color.white.opacity(0.035))
            .clipShape(RoundedRectangle(cornerRadius: 9))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(studio.isBusy)
    }

    private var mainContent: some View {
        VStack(spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text(workspace == .studio ? "Studio preview" : "Choose a feed")
                        .font(.title2.weight(.semibold))
                    Text(workspace == .studio
                         ? "Changes here are local until you start a broadcast."
                         : "Select which local feed you want to watch.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                if studio.isBroadcasting {
                    Label("LIVE", systemImage: "dot.radiowaves.left.and.right")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(.red)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                        .background(.red.opacity(0.12), in: Capsule())
                }
            }
            .padding(22)

            ZStack {
                RoundedRectangle(cornerRadius: 12).fill(.black)
                if studio.selectedFeedIsCamera && studio.isBroadcasting {
                    VStack(spacing: 10) {
                        Image(systemName: "dot.radiowaves.left.and.right")
                            .font(.system(size: 38))
                            .foregroundStyle(.red)
                        Text("Camera is live")
                            .font(.title3.weight(.semibold))
                        Text("The camera is sending video to your selected destinations. Its local preview returns when you stop.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                    }
                    .padding(30)
                } else if studio.selectedFeedIsCamera && studio.isCameraPreviewReady {
                    CapturePreview(session: studio.captureSession)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                } else if let player = studio.videoPlayer {
                    VideoPlayer(player: player)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                } else {
                    VStack(spacing: 10) {
                        Image(systemName: "play.rectangle")
                            .font(.system(size: 38))
                            .foregroundStyle(.secondary)
                        Text(studio.feeds.isEmpty
                             ? "Add a feed to get started"
                             : studio.selectedFeedIsCamera ? "Camera preview" : "Feed unavailable")
                            .font(.title3.weight(.semibold))
                        Text(studio.feeds.isEmpty
                             ? "Add a camera or import a local video."
                             : studio.selectedFeedIsCamera ? studio.status : "Choose another feed or reconnect the camera.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                    }
                    .padding(30)
                }
            }
            .padding(.horizontal, 22)
            .frame(maxHeight: .infinity)

            HStack(spacing: 9) {
                Circle().fill(studio.isBroadcasting ? .red : .green).frame(width: 8, height: 8)
                Text(studio.status)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
                Spacer()
            }
            .padding(18)
        }
    }

    private var broadcastSidebar: some View {
        VStack(alignment: .leading, spacing: 15) {
            Text("Stream destinations").font(.title3.weight(.semibold))
            Text("Set up one or more RTMP destinations. The selected feeds are sent simultaneously.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            ForEach(studio.destinations) { destination in
                destinationRow(destination)
            }

            Menu {
                ForEach(PlatformDestination.allCases) { platform in
                    Button("Set up \(platform.name)") {
                        edit(platform)
                    }
                }
                Divider()
                Button("Add custom RTMP destination", systemImage: "plus") {
                    let destination = StudioDestination(
                        id: UUID().uuidString,
                        name: "",
                        serverURL: "rtmp://",
                        isSelected: true
                    )
                    editingDestination = EditableDestination(destination: destination)
                }
            } label: {
                Label("Add destination", systemImage: "plus")
                    .frame(maxWidth: .infinity)
            }
            .disabled(studio.isBusy)

            Spacer(minLength: 0)

            if studio.selectedFeedIsCamera {
                Divider()
                Text("Broadcast audio").font(.headline)
                Picker("Microphone", selection: $studio.selectedMicrophoneID) {
                    if studio.microphones.isEmpty {
                        Text("No microphones found").tag("")
                    }
                    ForEach(studio.microphones) { microphone in
                        Text(microphone.name).tag(microphone.id)
                    }
                }
                .disabled(studio.microphones.isEmpty || studio.isBusy)
                Toggle("Include microphone audio", isOn: $studio.includeMicrophoneAudio)
                    .disabled(studio.microphones.isEmpty || studio.isBusy)
                    .onChange(of: studio.includeMicrophoneAudio) { _ in
                        studio.updateAudioSettings(
                            microphoneID: studio.selectedMicrophoneID,
                            enabled: studio.includeMicrophoneAudio
                        )
                    }
                    .onChange(of: studio.selectedMicrophoneID) { _ in
                        studio.updateAudioSettings(
                            microphoneID: studio.selectedMicrophoneID,
                            enabled: studio.includeMicrophoneAudio
                        )
                    }
            }

            Button {
                studio.startBroadcast()
            } label: {
                Label(
                    studio.isStartingBroadcast
                        ? "Starting broadcast…"
                        : studio.isBroadcasting ? "Stop broadcast" : "Start broadcast",
                    systemImage: studio.isBroadcasting ? "stop.fill" : "dot.radiowaves.left.and.right"
                )
                .frame(maxWidth: .infinity)
                .padding(.vertical, 3)
            }
            .buttonStyle(.borderedProminent)
            .tint(studio.isBroadcasting ? .red : .purple)
            .disabled(studio.isStartingBroadcast)

            Text("Stream keys are stored in Keychain. No broadcast starts until you choose a configured destination.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(18)
    }

    private func destinationRow(_ destination: StudioDestination) -> some View {
        let configured = studio.isDestinationConfigured(destination)
        return HStack(spacing: 9) {
            Toggle(destination.name, isOn: Binding(
                get: { studio.destinations.first(where: { $0.id == destination.id })?.isSelected ?? false },
                set: { studio.setDestination(destination, selected: $0) }
            ))
            .toggleStyle(.checkbox)
            .disabled(!configured || studio.isBusy)

            Spacer(minLength: 0)

            Text(configured ? "Configured" : "Setup required")
                .font(.caption2)
                .foregroundStyle(configured ? Color.green : Color.gray)

            Button("Setup") {
                editingDestination = EditableDestination(destination: destination)
            }
            .controlSize(.small)
            .disabled(studio.isBusy)
        }
        .padding(10)
        .background(Color.white.opacity(0.045), in: RoundedRectangle(cornerRadius: 9))
    }

    private func edit(_ platform: PlatformDestination) {
        editingDestination = studio.setUpPlatform(platform)
    }
}
