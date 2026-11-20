import Foundation
import Network

final class PinnedHttpRequest {
    private static let separator = Data("\r\n".utf8)
    private static let headerEnd = Data("\r\n\r\n".utf8)
    private let queue: DispatchQueue
    private let connection: NWConnection
    private let request: Data
    private let limit: Int
    private let verification: Verification
    private var response = Data()
    private var completion: ((Result<[String: Any], Error>) -> Void)?

    init(endpoint: PinnedEndpoint, path: String, json: String? = nil, token: String? = nil, queue: DispatchQueue) throws {
        let body = Data((json ?? "").utf8)
        let registration = path == "/notification-registration/v2"
        if let token {
            let validPath = path == "/session" || path == "/incidents"
                || path.range(of: #"^/incidents\?before=[0-9]{1,19}\.[a-fA-F0-9]{32}\.(warning|safeguard)$"#, options: .regularExpression) != nil
                || path.range(of: #"^/incidents/(warning|safeguard)/[a-fA-F0-9-]{36}$"#, options: .regularExpression) != nil
            guard (registration ? json != nil && body.count <= 1024 : validPath && json == nil),
                  path.utf8.allSatisfy({ (33...126).contains($0) }), isMobileToken(token) else {
                throw TransportError.invalidEndpoint
            }
        } else if !["/pairing", "/pairing/complete"].contains(path) || json == nil || body.count > 1024 {
            throw TransportError.invalidEndpoint
        }

        self.queue = queue
        limit = token == nil || registration ? 4096 : 512 * 1024
        let method = json == nil ? "GET" : "POST"
        var headers = [
            "\(method) \(path) HTTP/1.1",
            "Host: \(endpoint.host):\(endpoint.port.rawValue)",
            "Connection: close"
        ]
        if let token {
            headers.append("Authorization: Bearer \(token)")
        }
        if json != nil {
            headers.append("Content-Type: application/json")
            headers.append("Content-Length: \(body.count)")
        }
        headers += ["", ""]
        request = Data(headers.joined(separator: "\r\n").utf8) + body
        let verification = Verification()
        self.verification = verification
        let parameters = endpoint.parameters(queue: queue) { verification.keyMismatch = true }
        connection = NWConnection(host: NWEndpoint.Host(endpoint.host), port: endpoint.port, using: parameters)
    }

    func start(completion: @escaping (Result<[String: Any], Error>) -> Void) {
        self.completion = completion
        connection.stateUpdateHandler = { state in
            switch state {
            case .ready:
                self.send()
            case .failed:
                self.finish(.failure(self.verification.keyMismatch ? TransportError.keyMismatch : TransportError.connectionFailed))
            case .cancelled:
                self.finish(.failure(TransportError.connectionFailed))
            default:
                break
            }
        }
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 12) { [weak self] in
            guard let self else { return }
            if case .localNetworkDenied? = self.connection.currentPath?.unsatisfiedReason {
                self.finish(.failure(TransportError.localNetworkDenied))
            } else {
                self.finish(.failure(TransportError.timeout))
            }
        }
    }

    private func send() {
        connection.send(content: request, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            if error == nil {
                self.receive()
            } else {
                self.finish(.failure(TransportError.connectionFailed))
            }
        })
    }

    private func receive() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] bytes, _, done, error in
            guard let self else { return }

            if let bytes {
                self.response.append(bytes)
            }

            do {
                guard self.response.count <= self.limit + 12288 else { throw TransportError.invalidResponse }
                if let reply = try self.parseResponse(complete: done) {
                    self.finish(.success(reply))
                } else if done || error != nil {
                    self.finish(.failure(TransportError.invalidResponse))
                } else {
                    self.receive()
                }
            } catch {
                self.finish(.failure(error))
            }
        }
    }

    private func parseResponse(complete: Bool) throws -> [String: Any]? {
        guard let boundary = response.range(of: Self.headerEnd) else { return nil }
        guard let text = String(data: response[..<boundary.lowerBound], encoding: .ascii) else {
            throw TransportError.invalidResponse
        }
        let lines = text.components(separatedBy: "\r\n")
        let statusLine = lines[0].split(separator: " ", maxSplits: 2)
        guard statusLine.count >= 2, statusLine[0] == "HTTP/1.1", let status = Int(statusLine[1]),
              (200...599).contains(status) else {
            throw TransportError.invalidResponse
        }

        let body = Data(response[boundary.upperBound...])
        let headers = lines.dropFirst()
        let bodyBytes: Data

        if status == 204 || status == 304 {
            bodyBytes = Data()
        } else if headers.contains(where: { $0.lowercased() == "transfer-encoding: chunked" }) {
            guard let decoded = try decodeChunks(body) else { return nil }
            bodyBytes = decoded
        } else if let lengthHeader = headers.first(where: { $0.lowercased().hasPrefix("content-length:") }) {
            guard let length = Int(lengthHeader.dropFirst(15).trimmingCharacters(in: .whitespaces)),
                  (0...limit).contains(length) else {
                throw TransportError.invalidResponse
            }
            guard body.count >= length else { return nil }
            bodyBytes = Data(body.prefix(length))
        } else {
            guard complete else { return nil }
            bodyBytes = body
        }

        guard bodyBytes.count <= limit, let result = String(data: bodyBytes, encoding: .utf8) else {
            throw TransportError.invalidResponse
        }
        return ["status": status, "body": result]
    }

    private func decodeChunks(_ encoded: Data) throws -> Data? {
        var result = Data()
        var cursor = encoded.startIndex

        while let line = encoded.range(of: Self.separator, in: cursor..<encoded.endIndex) {
            guard let sizeText = String(data: encoded[cursor..<line.lowerBound], encoding: .ascii),
                  let size = Int(sizeText, radix: 16), (0...limit - result.count).contains(size) else {
                throw TransportError.invalidResponse
            }
            if size == 0 {
                return result
            }

            let next = line.upperBound + size
            guard next + 2 <= encoded.endIndex else { return nil }
            guard encoded[next..<(next + 2)].elementsEqual(Self.separator) else { throw TransportError.invalidResponse }
            result.append(encoded[line.upperBound..<next])
            cursor = next + 2
        }

        return nil
    }

    private func finish(_ result: Result<[String: Any], Error>) {
        guard let completion else { return }
        self.completion = nil
        connection.stateUpdateHandler = nil
        connection.cancel()
        completion(result)
    }

    private final class Verification {
        var keyMismatch = false
    }
}
