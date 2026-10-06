import SwiftUI

struct DestinationEditor: View {
    @Environment(\.dismiss) private var dismiss
    @State private var name: String
    @State private var serverURL: String
    @State private var streamKey = ""
    @State private var errorMessage: String?

    let destination: StudioDestination
    let onSave: (String, String, String, String?) throws -> Void

    init(
        destination: StudioDestination,
        onSave: @escaping (String, String, String, String?) throws -> Void
    ) {
        self.destination = destination
        self.onSave = onSave
        _name = State(initialValue: destination.name)
        _serverURL = State(initialValue: destination.serverURL)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Set up \(destination.name)")
                .font(.title2.weight(.semibold))
            Text("Enter the RTMP server URL without the stream key. Keys are stored in your macOS Keychain.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if destination.id != PlatformDestination.youtube.id,
               destination.id != PlatformDestination.twitch.id {
                field("Destination name") {
                    TextField("Destination name", text: $name)
                }
            }
            field("RTMP server URL") {
                TextField("rtmps://server.example/app", text: $serverURL)
                    .textFieldStyle(.roundedBorder)
            }
            field("Stream key") {
                SecureField("Stream key", text: $streamKey)
                    .textFieldStyle(.roundedBorder)
            }
            if !streamKey.isEmpty {
                Text("Your stream key is saved securely in Keychain.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else if destination.id != PlatformDestination.youtube.id,
                      destination.id != PlatformDestination.twitch.id {
                Text("Enter a stream key to save this destination.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                Text("Leave the key blank to keep the key already stored in Keychain.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            if let errorMessage {
                Text(errorMessage)
                    .font(.caption)
                    .foregroundStyle(.red)
            }

            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                Button("Save", action: save)
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(22)
        .frame(width: 440)
    }

    private func field<Content: View>(
        _ title: String,
        @ViewBuilder content: () -> Content
    ) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(.callout.weight(.medium))
            content()
        }
    }

    private func save() {
        do {
            try onSave(name, serverURL, streamKey, destination.id)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
