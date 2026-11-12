import Foundation
import Network

final class PairingRequest {
    private static let separator = Data("\r\n".utf8)
    private static let headerEnd = Data("\r\n\r\n".utf8)
    private let queue: DispatchQueue
    private let connection: NWConnection
    private let request: Data
    private var response = Data()
    private var completion: ((Result<[String: Any], Error>) -> Void)?

    init(endpoint: PinnedEndpoint, path: String, json: String, queue: DispatchQueue) throws {
        let body = Data(json.utf8)
        guard ["/pairing", "/pairing/complete"].contains(path), body.count <= 1024 else {
            throw TransportError.invalidEndpoint
        }

        self.queue = queue
        connection = NWConnection(host: NWEndpoint.Host(endpoint.host), port: endpoint.port, using: endpoint.parameters(queue: queue))

        let headers = [
            "POST \(path) HTTP/1.1",
            "Host: \(endpoint.host):\(endpoint.port.rawValue)",
            "Content-Type: application/json",
            "Content-Length: \(body.count)",
            "Connection: close", "", ""
        ]
        request = Data(headers.joined(separator: "\r\n").utf8) + body
    }

    func start(completion: @escaping (Result<[String: Any], Error>) -> Void) {
        self.completion = completion
        connection.stateUpdateHandler = { state in
            switch state {
            case .ready:
                self.send()
            case .failed:
                self.finish(.failure(TransportError.connectionFailed))
            case .cancelled:
                self.finish(.failure(TransportError.connectionFailed))
            default:
                break
            }
        }
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 12) { [weak self] in
            self?.finish(.failure(TransportError.timeout))
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
                guard self.response.count <= 16384 else { throw TransportError.invalidResponse }
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

        if headers.contains(where: { $0.lowercased() == "transfer-encoding: chunked" }) {
            guard let decoded = try decodeChunks(body) else { return nil }
            bodyBytes = decoded
        } else if let lengthHeader = headers.first(where: { $0.lowercased().hasPrefix("content-length:") }) {
            guard let length = Int(lengthHeader.dropFirst(15).trimmingCharacters(in: .whitespaces)),
                  (0...4096).contains(length) else {
                throw TransportError.invalidResponse
            }
            guard body.count >= length else { return nil }
            bodyBytes = Data(body.prefix(length))
        } else {
            guard complete else { return nil }
            bodyBytes = body
        }

        guard bodyBytes.count <= 4096, let result = String(data: bodyBytes, encoding: .utf8) else {
            throw TransportError.invalidResponse
        }
        return ["status": status, "body": result]
    }

    private func decodeChunks(_ encoded: Data) throws -> Data? {
        var result = Data()
        var cursor = encoded.startIndex

        while let line = encoded.range(of: Self.separator, in: cursor..<encoded.endIndex) {
            guard let sizeText = String(data: encoded[cursor..<line.lowerBound], encoding: .ascii),
                  let size = Int(sizeText, radix: 16), (0...4096 - result.count).contains(size) else {
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
}
