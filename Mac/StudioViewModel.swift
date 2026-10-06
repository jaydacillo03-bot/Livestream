import AVFoundation
import AVKit
import AppKit
import Combine
import Foundation
import Security
import UniformTypeIdentifiers

struct StudioFeed: Codable, Identifiable, Equatable {
    enum Kind: String, Codable {
        case camera
        case video
    }

    var id: String
    var title: String
    var kind: Kind
    var cameraID: String?
    var videoPath: String?
}

struct StudioDestination: Codable, Identifiable, Equatable {
    var id: String
    var name: String
    var serverURL: String
    var isSelected: Bool
}

private struct StudioConfiguration: Codable {
    var feeds: [StudioFeed]
    var destinations: [StudioDestination]
    var selectedFeedID: String?
    var microphoneID: String?
    var includeMicrophoneAudio: Bool
}

struct CaptureDevice: Identifiable, Equatable {
    var id: String
    var name: String
}

@MainActor
final class StudioViewModel: ObservableObject {
    @Published private(set) var feeds: [StudioFeed] = []
    @Published private(set) var cameras: [CaptureDevice] = []
    @Published private(set) var microphones: [CaptureDevice] = []
    @Published private(set) var destinations: [StudioDestination] = []
    @Published private(set) var configuredDestinationIDs: Set<String> = []
    @Published var selectedFeedID: String?
    @Published var selectedCameraID = ""
    @Published var selectedMicrophoneID = ""
    @Published var includeMicrophoneAudio = false
    @Published var isBroadcasting = false
    @Published var isStartingBroadcast = false
    @Published private(set) var isCameraPreviewReady = false
    @Published var status = "Refresh cameras to get started."
    @Published var videoPlayer: AVPlayer?

    let captureSession = AVCaptureSession()
    private var broadcastProcess: Process?
    private var outputPipe: Pipe?
    private var errorPipe: Pipe?
    private var inputPipe: Pipe?
    private var isShuttingDown = false
    private var configurationLoadError: String?
    private var destinationKeyError: String?
    private let captureQueue = DispatchQueue(label: "com.mosaicstudio.mac.capture", qos: .userInitiated)
    private var configurationURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("MosaicStudioMac", isDirectory: true)
            .appendingPathComponent("studio.json")
    }

    var selectedFeed: StudioFeed? {
        feeds.first { $0.id == selectedFeedID }
    }

    var selectedFeedIsCamera: Bool {
        selectedFeed?.kind == .camera
    }

    var isBusy: Bool {
        isStartingBroadcast || isBroadcasting
    }

    init() {
        loadConfiguration()
        refreshDestinationKeyStates()
    }

    func refreshDevices() async {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            break
        case .notDetermined:
            guard await AVCaptureDevice.requestAccess(for: .video) else {
                status = "Camera access was denied. Allow it in System Settings > Privacy & Security > Camera."
                return
            }
        case .denied, .restricted:
            status = "Allow camera access in System Settings > Privacy & Security > Camera, then refresh."
            return
        @unknown default:
            status = "macOS returned an unknown camera permission state."
            return
        }

        cameras = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.builtInWideAngleCamera, .externalUnknown],
            mediaType: .video,
            position: .unspecified
        ).devices.map { CaptureDevice(id: $0.uniqueID, name: $0.localizedName) }

        microphones = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.builtInMicrophone, .externalUnknown],
            mediaType: .audio,
            position: .unspecified
        ).devices.map { CaptureDevice(id: $0.uniqueID, name: $0.localizedName) }

        if !cameras.contains(where: { $0.id == selectedCameraID }) {
            selectedCameraID = cameras.first?.id ?? ""
        }
        if !microphones.contains(where: { $0.id == selectedMicrophoneID }) {
            selectedMicrophoneID = microphones.first?.id ?? ""
        }
        status = configurationLoadError ?? destinationKeyError ?? (cameras.isEmpty
            ? "No camera is connected. Connect one and choose Refresh cameras."
            : "Found \(cameras.count) camera\(cameras.count == 1 ? "" : "s").")
        if configurationLoadError == nil {
            saveConfiguration()
        }
    }

    func addSelectedCamera() {
        guard !isBusy else { return }
        guard let camera = AVCaptureDevice(uniqueID: selectedCameraID) else {
            status = "Select a connected camera before adding a feed."
            return
        }

        let feed = StudioFeed(
            id: UUID().uuidString,
            title: camera.localizedName,
            kind: .camera,
            cameraID: camera.uniqueID,
            videoPath: nil
        )
        feeds.append(feed)
        selectFeed(feed)
        saveConfiguration()
    }

    func importVideo() {
        guard !isBusy else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.movie]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.prompt = "Add video feed"
        guard panel.runModal() == .OK, let url = panel.url else { return }

        let feed = StudioFeed(
            id: UUID().uuidString,
            title: url.deletingPathExtension().lastPathComponent,
            kind: .video,
            cameraID: nil,
            videoPath: url.path
        )
        feeds.append(feed)
        selectFeed(feed)
        saveConfiguration()
    }

    func selectFeed(_ feed: StudioFeed) {
        guard !isBusy else {
            status = "Stop the broadcast before changing feeds."
            return
        }

        selectedFeedID = feed.id
        stopCameraPreview()
        videoPlayer = nil

        if feed.kind == .camera {
            guard let deviceID = feed.cameraID,
                  let camera = AVCaptureDevice(uniqueID: deviceID) else {
                status = "Reconnect this camera and choose Refresh cameras."
                return
            }
            startCameraPreview(camera)
        } else if let path = feed.videoPath, FileManager.default.fileExists(atPath: path) {
            let player = AVPlayer(url: URL(fileURLWithPath: path))
            videoPlayer = player
            player.play()
            status = "Playing \(feed.title) in local preview. Not broadcasting."
        } else {
            status = "The video file is missing. Remove and reimport this feed."
        }
        saveConfiguration()
    }

    func setDestination(_ destination: StudioDestination, selected: Bool) {
        guard !isBusy,
              let index = destinations.firstIndex(where: { $0.id == destination.id }) else { return }
        destinations[index].isSelected = selected
        saveConfiguration()
    }

    func updateAudioSettings(microphoneID: String, enabled: Bool) {
        guard !isBusy else { return }
        selectedMicrophoneID = microphoneID
        includeMicrophoneAudio = enabled
        saveConfiguration()
    }

    func removeSelectedFeed() {
        guard !isBusy, let selectedFeed else { return }
        feeds.removeAll { $0.id == selectedFeed.id }
        self.selectedFeedID = feeds.first?.id
        if let nextFeed = selectedFeed {
            selectFeed(nextFeed)
        } else {
            stopCameraPreview()
            videoPlayer = nil
            status = "Add a camera or import a video feed to get started."
        }
        saveConfiguration()
    }

    func saveDestination(name: String, serverURL: String, streamKey: String, id: String?) throws {
        let cleanName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let cleanURL = serverURL.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !cleanName.isEmpty else {
            throw StudioError.message("Enter a destination name.")
        }
        guard let components = URLComponents(string: cleanURL),
              ["rtmp", "rtmps"].contains(components.scheme?.lowercased() ?? ""),
              components.host?.isEmpty == false,
              components.user == nil, components.password == nil,
              components.query == nil, components.fragment == nil else {
            throw StudioError.message("Enter a valid rtmp:// or rtmps:// server URL without a stream key.")
        }

        let destinationID = id ?? UUID().uuidString
        if destinations.contains(where: {
            $0.id != destinationID
                && $0.name.localizedCaseInsensitiveCompare(cleanName) == .orderedSame
        }) {
            throw StudioError.message("A destination with this name already exists.")
        }
        let trimmedKey = streamKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let existingKey = try KeychainStore.load(account: destinationID)
        if !trimmedKey.isEmpty {
            try KeychainStore.save(key: trimmedKey, account: destinationID)
        } else if existingKey == nil {
            throw StudioError.message("Enter the stream key provided by your streaming platform.")
        }

        if let index = destinations.firstIndex(where: { $0.id == destinationID }) {
            destinations[index].name = cleanName
            destinations[index].serverURL = cleanURL
        } else {
            destinations.append(
                StudioDestination(id: destinationID, name: cleanName, serverURL: cleanURL, isSelected: true)
            )
        }
        refreshDestinationKeyStates()
        saveConfiguration()
    }

    func shutdown() {
        isShuttingDown = true
        if isBroadcasting {
            stopBroadcast()
        }
        stopCameraPreview()
    }

    func setUpPlatform(_ platform: PlatformDestination) -> EditableDestination {
        if let existing = destinations.first(where: { $0.id == platform.id }) {
            return EditableDestination(destination: existing)
        }
        return EditableDestination(
            destination: StudioDestination(
                id: platform.id,
                name: platform.name,
                serverURL: platform.serverURL,
                isSelected: false
            )
        )
    }

    func isDestinationConfigured(_ destination: StudioDestination) -> Bool {
        configuredDestinationIDs.contains(destination.id)
    }

    func startBroadcast() {
        guard !isShuttingDown else { return }
        guard !isBroadcasting else {
            stopBroadcast()
            return
        }
        guard !isStartingBroadcast else { return }

        guard let feed = selectedFeed else {
            status = "Select a camera or video feed before starting a broadcast."
            return
        }
        let targets = destinations.filter(\.isSelected)
        guard !targets.isEmpty else {
            status = "Set up a destination and select it before starting a broadcast."
            return
        }
        for target in targets where !isDestinationConfigured(target) {
            status = "Set up the stream key for \(target.name) before broadcasting."
            return
        }
        guard let ffmpeg = Self.findFFmpeg() else {
            status = "FFmpeg was not found. Install it with `brew install ffmpeg`, then restart Mosaic Studio."
            return
        }

        isStartingBroadcast = true
        Task { await launchBroadcast(ffmpeg: ffmpeg, feed: feed, targets: targets) }
    }

    private func launchBroadcast(
        ffmpeg: String,
        feed: StudioFeed,
        targets: [StudioDestination]
    ) async {
        defer { isStartingBroadcast = false }
        do {
            guard !isShuttingDown else { return }
            let arguments = try makeFFmpegArguments(
                executable: ffmpeg,
                feed: feed,
                destinations: targets
            )
            if feed.kind == .camera {
                status = "Opening the camera for broadcast…"
                await stopCameraPreviewAndWait()
            }
            guard !isShuttingDown else {
                status = "Broadcast cancelled."
                return
            }
            let process = Process()
            let input = Pipe()
            let progress = Pipe()
            let errors = Pipe()
            process.executableURL = URL(fileURLWithPath: ffmpeg)
            process.arguments = arguments
            process.standardInput = input
            process.standardOutput = progress
            process.standardError = errors

            progress.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let data = handle.availableData
                guard !data.isEmpty, let text = String(data: data, encoding: .utf8) else { return }
                Task { @MainActor [weak self] in
                    self?.handleFFmpegProgress(text, process: process, targets: targets)
                }
            }
            errors.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let data = handle.availableData
                guard !data.isEmpty, let text = String(data: data, encoding: .utf8) else { return }
                Task { @MainActor [weak self] in
                    self?.handleFFmpegOutput(text, process: process, targets: targets)
                }
            }
            process.terminationHandler = { [weak self] endedProcess in
                Task { @MainActor [weak self] in
                    self?.finishBroadcast(endedProcess)
                }
            }

            try process.run()
            broadcastProcess = process
            inputPipe = input
            outputPipe = progress
            errorPipe = errors
            isBroadcasting = true
            status = "Starting broadcast to \(targets.map(\.name).joined(separator: ", "))…"
        } catch {
            status = "Couldn't start FFmpeg: \(error.localizedDescription)"
            if feed.kind == .camera,
               let cameraID = feed.cameraID,
               let camera = AVCaptureDevice(uniqueID: cameraID) {
                startCameraPreview(camera)
            }
        }
    }

    func stopBroadcast() {
        guard isBroadcasting, let broadcastProcess else { return }
        status = "Stopping broadcast…"
        if let inputPipe {
            do {
                try inputPipe.fileHandleForWriting.write(contentsOf: Data("q\n".utf8))
                try inputPipe.fileHandleForWriting.close()
            } catch {
                status = "Couldn't send FFmpeg the stop command: \(error.localizedDescription)"
                broadcastProcess.terminate()
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 8) { [weak self, weak broadcastProcess] in
                guard let self, let broadcastProcess,
                      self.broadcastProcess === broadcastProcess,
                      broadcastProcess.isRunning else { return }
                self.status = "FFmpeg didn't stop after the quit command; terminating it."
                broadcastProcess.terminate()
            }
        } else {
            broadcastProcess.terminate()
        }
    }

    private func startCameraPreview(_ camera: AVCaptureDevice) {
        guard !isShuttingDown else { return }
        isCameraPreviewReady = false
        status = "Connecting to \(camera.localizedName)…"
        captureQueue.async { [captureSession] in
            do {
                let input = try AVCaptureDeviceInput(device: camera)
                captureSession.beginConfiguration()
                captureSession.inputs.forEach { captureSession.removeInput($0) }
                guard captureSession.canAddInput(input) else {
                    captureSession.commitConfiguration()
                    Task { @MainActor [weak self] in
                        self?.status = "macOS couldn't add \(camera.localizedName) to the preview."
                    }
                    return
                }
                captureSession.addInput(input)
                captureSession.commitConfiguration()
                if !captureSession.isRunning {
                    captureSession.startRunning()
                }
                Task { @MainActor [weak self] in
                    self?.isCameraPreviewReady = true
                    self?.status = "Live preview from \(camera.localizedName). Nothing is being broadcast."
                }
            } catch {
                Task { @MainActor [weak self] in
                    self?.isCameraPreviewReady = false
                    self?.status = "Couldn't start camera preview: \(error.localizedDescription)"
                }
            }
        }
    }

    private func stopCameraPreview() {
        isCameraPreviewReady = false
        captureQueue.async { [captureSession] in
            if captureSession.isRunning {
                captureSession.stopRunning()
            }
        }
    }

    private func stopCameraPreviewAndWait() async {
        isCameraPreviewReady = false
        await withCheckedContinuation { continuation in
            captureQueue.async { [captureSession] in
                if captureSession.isRunning {
                    captureSession.stopRunning()
                }
                continuation.resume()
            }
        }
    }

    private func makeFFmpegArguments(
        executable: String,
        feed: StudioFeed,
        destinations selectedDestinations: [StudioDestination]
    ) throws -> [String] {
        var arguments = [
            "-hide_banner", "-loglevel", "warning", "-nostats",
            "-stats_period", "1", "-progress", "pipe:1"
        ]

        if feed.kind == .camera {
            guard let cameraID = feed.cameraID,
                  let camera = AVCaptureDevice(uniqueID: cameraID) else {
                throw StudioError.message("Reconnect the selected camera and refresh the camera list.")
            }
            let devices = try Self.listAVFoundationDevices(executable: executable)
            guard let cameraDevice = devices.video.first(where: {
                $0.name.localizedCaseInsensitiveCompare(camera.localizedName) == .orderedSame
            }) else {
                throw StudioError.message("FFmpeg could not match \(camera.localizedName). Check camera permission and refresh.")
            }
            arguments += ["-f", "avfoundation", "-framerate", "30", "-video_size", "1280x720"]

            let microphone = includeMicrophoneAudio
                ? microphones.first(where: { $0.id == selectedMicrophoneID })
                : nil
            if includeMicrophoneAudio, microphone == nil {
                throw StudioError.message("Select an available microphone or turn off microphone audio.")
            }
            let audioIndex: String
            if let microphone {
                guard let device = devices.audio.first(where: {
                    $0.name.localizedCaseInsensitiveCompare(microphone.name) == .orderedSame
                }) else {
                    throw StudioError.message("FFmpeg could not match microphone \(microphone.name).")
                }
                audioIndex = String(device.index)
            } else {
                audioIndex = "none"
            }
            arguments += ["-i", "\(cameraDevice.index):\(audioIndex)"]
        } else if let path = feed.videoPath, FileManager.default.fileExists(atPath: path) {
            arguments += ["-re", "-stream_loop", "-1", "-i", path]
        } else {
            throw StudioError.message("The selected video file is missing.")
        }

        arguments += [
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-b:v", "4500k",
            "-maxrate", "4500k",
            "-bufsize", "9000k",
            "-g", "60",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "160k",
            "-ar", "44100",
            "-f", "tee"
        ]

        let outputs = try selectedDestinations.map { destination -> String in
            guard let key = try KeychainStore.load(account: destination.id) else {
                throw StudioError.message("The stream key for \(destination.name) is missing from Keychain.")
            }
            let url = "\(destination.serverURL.trimmingCharacters(in: CharacterSet(charactersIn: "/")))/\(key)"
            return "[f=flv:onfail=abort]\(Self.escapeTeeTarget(url))"
        }
        arguments.append(outputs.joined(separator: "|"))
        return arguments
    }

    private func handleFFmpegOutput(
        _ text: String,
        process: Process,
        targets: [StudioDestination]
    ) {
        guard broadcastProcess === process else { return }
        var safeText = text
        for target in targets {
            let key: String?
            do {
                key = try KeychainStore.load(account: target.id)
            } catch {
                status = "FFmpeg reported an error, but macOS Keychain couldn't read the stream key for safe redaction."
                return
            }
            guard let key, !key.isEmpty else {
                status = "FFmpeg reported an error, but macOS Keychain couldn't read the stream key for safe redaction."
                return
            }
            safeText = safeText.replacingOccurrences(of: key, with: "[redacted]")
        }
        let compact = safeText.trimmingCharacters(in: .whitespacesAndNewlines)
        if compact.localizedCaseInsensitiveContains("error")
            || compact.localizedCaseInsensitiveContains("failed")
            || compact.localizedCaseInsensitiveContains("connection refused") {
            status = String(compact.suffix(320))
        }
    }

    private func handleFFmpegProgress(
        _ text: String,
        process: Process,
        targets: [StudioDestination]
    ) {
        guard broadcastProcess === process else { return }
        if text.contains("progress=continue") {
            status = "LIVE — broadcasting to \(targets.map(\.name).joined(separator: ", "))."
        }
    }

    private func finishBroadcast(_ process: Process) {
        guard broadcastProcess === process else { return }
        outputPipe?.fileHandleForReading.readabilityHandler = nil
        errorPipe?.fileHandleForReading.readabilityHandler = nil
        let exitCode = process.terminationStatus
        broadcastProcess = nil
        outputPipe = nil
        errorPipe = nil
        inputPipe = nil
        isBroadcasting = false
        if exitCode == 0 {
            status = "Broadcast stopped."
        } else {
            status = "Broadcast stopped (FFmpeg exit code \(exitCode)). \(status)"
        }
        if !isShuttingDown,
           let selectedFeed, selectedFeed.kind == .camera,
           let cameraID = selectedFeed.cameraID,
           let camera = AVCaptureDevice(uniqueID: cameraID) {
            startCameraPreview(camera)
        }
    }

    private func loadConfiguration() {
        guard FileManager.default.fileExists(atPath: configurationURL.path) else {
            return
        }
        do {
            let data = try Data(contentsOf: configurationURL)
            let configuration = try JSONDecoder().decode(StudioConfiguration.self, from: data)
            feeds = configuration.feeds
            destinations = configuration.destinations
            selectedFeedID = configuration.selectedFeedID
            selectedMicrophoneID = configuration.microphoneID ?? ""
            includeMicrophoneAudio = configuration.includeMicrophoneAudio
        } catch {
            configurationLoadError = "Couldn't load saved studio settings: \(error.localizedDescription)"
            status = configurationLoadError ?? "Couldn't load saved studio settings."
        }
    }

    private func refreshDestinationKeyStates() {
        var configuredIDs: Set<String> = []
        destinationKeyError = nil
        for destination in destinations {
            do {
                if try KeychainStore.load(account: destination.id) != nil {
                    configuredIDs.insert(destination.id)
                }
            } catch {
                destinationKeyError = "Couldn't access a saved stream key in macOS Keychain: \(error.localizedDescription)"
            }
        }
        configuredDestinationIDs = configuredIDs
        if let destinationKeyError {
            status = destinationKeyError
        }
    }

    private func saveConfiguration() {
        do {
            try FileManager.default.createDirectory(
                at: configurationURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            let configuration = StudioConfiguration(
                feeds: feeds,
                destinations: destinations,
                selectedFeedID: selectedFeedID,
                microphoneID: selectedMicrophoneID,
                includeMicrophoneAudio: includeMicrophoneAudio
            )
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(configuration).write(to: configurationURL, options: .atomic)
            configurationLoadError = nil
        } catch {
            status = "Couldn't save studio settings: \(error.localizedDescription)"
        }
    }

    private static func findFFmpeg() -> String? {
        let paths = [
            "/opt/homebrew/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/opt/local/bin/ffmpeg"
        ]
        if let path = paths.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) {
            return path
        }
        for directory in (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":") {
            let path = URL(fileURLWithPath: String(directory)).appendingPathComponent("ffmpeg").path
            if FileManager.default.isExecutableFile(atPath: path) { return path }
        }
        return nil
    }

    private static func listAVFoundationDevices(executable: String) throws
        -> (video: [FFmpegDevice], audio: [FFmpegDevice]) {
        let process = Process()
        let pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = ["-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", ""]
        process.standardError = pipe
        process.standardOutput = Pipe()
        try process.run()
        let output = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let log = String(decoding: output, as: UTF8.self)
        guard log.localizedCaseInsensitiveContains("AVFoundation video devices") else {
            throw StudioError.message("FFmpeg doesn't expose AVFoundation capture devices. Install a macOS FFmpeg build with AVFoundation support.")
        }

        var section: String?
        var video: [FFmpegDevice] = []
        var audio: [FFmpegDevice] = []
        let pattern = try NSRegularExpression(pattern: #"\[(\d+)\]\s+(.+)$"#)
        for line in log.components(separatedBy: .newlines) {
            if line.localizedCaseInsensitiveContains("AVFoundation video devices") {
                section = "video"
            } else if line.localizedCaseInsensitiveContains("AVFoundation audio devices") {
                section = "audio"
            } else if let section,
                      let match = pattern.firstMatch(
                        in: line,
                        range: NSRange(line.startIndex..., in: line)
                      ),
                      let indexRange = Range(match.range(at: 1), in: line),
                      let nameRange = Range(match.range(at: 2), in: line),
                      let index = Int(line[indexRange]) {
                let device = FFmpegDevice(
                    index: index,
                    name: String(line[nameRange]).trimmingCharacters(in: .whitespacesAndNewlines)
                )
                if section == "video" { video.append(device) } else { audio.append(device) }
            }
        }
        return (video, audio)
    }

    private static func escapeTeeTarget(_ target: String) -> String {
        var escaped = ""
        for character in target {
            if "\\|[]'".contains(character) { escaped.append("\\") }
            escaped.append(character)
        }
        return escaped
    }

}

struct EditableDestination: Identifiable {
    var destination: StudioDestination
    var id: String { destination.id }
}

enum PlatformDestination: String, CaseIterable, Identifiable {
    case youtube
    case twitch

    var id: String { rawValue }
    var name: String { rawValue == "youtube" ? "YouTube" : "Twitch" }
    var serverURL: String {
        rawValue == "youtube"
            ? "rtmps://a.rtmps.youtube.com/live2"
            : "rtmps://live.twitch.tv:443/app"
    }
}

private struct FFmpegDevice {
    var index: Int
    var name: String
}

enum StudioError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        if case let .message(message) = self { return message }
        return nil
    }
}

private enum KeychainStore {
    private static let service = "com.mosaicstudio.mac.stream-key"

    static func save(key: String, account: String) throws {
        let data = Data(key.utf8)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        let update = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if update == errSecSuccess { return }
        guard update == errSecItemNotFound else { throw keychainError(update) }
        var insert = query
        insert[kSecValueData as String] = data
        insert[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        let result = SecItemAdd(insert as CFDictionary, nil)
        guard result == errSecSuccess else { throw keychainError(result) }
    }

    static func load(account: String) throws -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else { throw keychainError(status) }
        return String(data: data, encoding: .utf8)
    }

    private static func keychainError(_ status: OSStatus) -> NSError {
        NSError(
            domain: NSOSStatusErrorDomain,
            code: Int(status),
            userInfo: [NSLocalizedDescriptionKey: SecCopyErrorMessageString(status, nil) as String? ?? "Keychain error"]
        )
    }
}
