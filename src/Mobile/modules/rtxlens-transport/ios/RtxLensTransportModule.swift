import ExpoModulesCore
import Foundation
import Network

public final class RtxLensTransportModule: Module {
    private let queue = DispatchQueue(label: "com.rtxlens.mobile.transport")
    private var stream: (id: String, connection: NWConnection, connected: Bool)?

    public func definition() -> ModuleDefinition {
        Name("RtxLensTransport")
        Events("onStreamState", "onFrame")

        AsyncFunction("post") { (address: String, pin: String, path: String, json: String, promise: Promise) in
            self.queue.async {
                do {
                    let endpoint = try PinnedEndpoint(address, fingerprint: pin)
                    let request = try PairingRequest(endpoint: endpoint, path: path, json: json, queue: self.queue)
                    request.start { result in
                        switch result {
                        case .success(let reply): promise.resolve(reply)
                        case .failure(let error): promise.reject("PAIRING_FAILED", error.localizedDescription)
                        }
                    }
                } catch {
                    promise.reject("PAIRING_FAILED", error.localizedDescription)
                }
            }
        }

        Function("openStream") { (id: String, address: String, pin: String, token: String) in
            self.queue.async { self.openStream(id: id, address: address, pin: pin, token: token) }
        }

        Function("closeStream") { (id: String) in
            self.queue.async {
                if self.stream?.id == id {
                    self.cancelStream()
                }
            }
        }

        OnDestroy {
            self.queue.async { self.cancelStream() }
        }
    }

    private func openStream(id: String, address: String, pin: String, token: String) {
        cancelStream()

        do {
            guard token.count == 76, token.range(of: "^[a-fA-F0-9]{32}\\.[A-Za-z0-9_-]{43}$", options: .regularExpression) != nil else {
                throw TransportError.invalidEndpoint
            }
            let endpoint = try PinnedEndpoint(address, fingerprint: pin)
            let parameters = endpoint.parameters(queue: queue)
            let websocket = NWProtocolWebSocket.Options()
            websocket.autoReplyPing = true
            websocket.maximumMessageSize = 1024 * 1024
            websocket.setAdditionalHeaders([(name: "Authorization", value: "Bearer \(token)")])
            parameters.defaultProtocolStack.applicationProtocols.insert(websocket, at: 0)

            let connection = NWConnection(to: try endpoint.sensorEndpoint(), using: parameters)
            stream = (id, connection, false)
            connection.stateUpdateHandler = { [weak self, weak connection] state in
                guard let self, let connection, self.stream?.connection === connection else { return }

                switch state {
                case .ready:
                    self.stream?.connected = true
                    self.emitState(id: id, state: "connected")
                    self.receive(id: id, connection: connection)
                case .failed, .cancelled:
                    self.finishStream(id: id, error: .connectionFailed)
                default:
                    break
                }
            }
            connection.start(queue: queue)
            queue.asyncAfter(deadline: .now() + 12) { [weak self, weak connection] in
                if let self, let connection, self.stream?.connection === connection, self.stream?.connected == false {
                    self.finishStream(id: id, error: .timeout)
                }
            }
        } catch {
            emitState(id: id, state: "error", message: error.localizedDescription)
        }
    }

    private func receive(id: String, connection: NWConnection) {
        connection.receiveMessage { [weak self, weak connection] data, context, _, error in
            guard let self, let connection, self.stream?.connection === connection else { return }
            if error != nil {
                self.finishStream(id: id, error: .connectionFailed)
                return
            }

            let metadata = context?.protocolMetadata(definition: NWProtocolWebSocket.definition) as? NWProtocolWebSocket.Metadata
            if metadata?.opcode == .close || context == nil {
                self.finishStream(id: id)
                return
            }

            if metadata?.opcode == .text, let data, let json = String(data: data, encoding: .utf8) {
                self.sendEvent("onFrame", ["id": id, "json": json])
            }
            self.receive(id: id, connection: connection)
        }
    }

    private func finishStream(id: String, error: TransportError? = nil) {
        cancelStream()
        emitState(id: id, state: error == nil ? "closed" : "error", message: error?.localizedDescription)
    }

    private func cancelStream() {
        let connection = stream?.connection
        stream = nil
        connection?.stateUpdateHandler = nil
        connection?.cancel()
    }

    private func emitState(id: String, state: String, message: String? = nil) {
        var event = ["id": id, "state": state]
        event["message"] = message
        sendEvent("onStreamState", event)
    }
}
