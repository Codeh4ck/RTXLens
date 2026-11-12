import CryptoKit
import Foundation
import Network
import Security

enum TransportError: LocalizedError {
    case invalidEndpoint
    case invalidResponse
    case connectionFailed
    case timeout

    var errorDescription: String? {
        switch self {
        case .invalidEndpoint: return "The paired PC address or public key is invalid."
        case .invalidResponse: return "The PC sent an invalid pairing response."
        case .connectionFailed: return "Could not connect securely to the PC. Check its address and pairing key."
        case .timeout: return "The PC did not respond in time."
        }
    }
}

struct PinnedEndpoint {
    private let components: URLComponents
    private let fingerprint: String
    let port: NWEndpoint.Port
    let host: String

    init(_ address: String, fingerprint: String) throws {
        guard let components = URLComponents(string: address),
              components.scheme == "https", components.user == nil, components.password == nil,
              components.path.isEmpty || components.path == "/",
              components.query == nil, components.fragment == nil,
              let host = components.host, !host.isEmpty, host.count <= 253,
              host.range(of: "^[A-Za-z0-9.-]+$", options: .regularExpression) != nil,
              let portNumber = components.port, (1024...65535).contains(portNumber),
              let port = NWEndpoint.Port(rawValue: UInt16(portNumber)),
              fingerprint.range(of: "^[A-Fa-f0-9]{64}$", options: .regularExpression) != nil else {
            throw TransportError.invalidEndpoint
        }

        self.components = components
        self.fingerprint = fingerprint.lowercased()
        self.port = port
        self.host = host
    }

    func sensorEndpoint() throws -> NWEndpoint {
        var url = components
        url.scheme = "wss"
        url.path = "/sensors"
        guard let address = url.url else { throw TransportError.invalidEndpoint }
        return .url(address)
    }

    func parameters(queue: DispatchQueue) -> NWParameters {
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
            complete(Self.matchesPin(sec_trust_copy_ref(trust).takeRetainedValue(), fingerprint: fingerprint))
        }, queue)

        return NWParameters(tls: tls, tcp: NWProtocolTCP.Options())
    }

    private static func matchesPin(_ trust: SecTrust, fingerprint: String) -> Bool {
        guard SecTrustSetNetworkFetchAllowed(trust, false) == errSecSuccess,
              let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let leaf = chain.first, let key = SecCertificateCopyKey(leaf),
              let attributes = SecKeyCopyAttributes(key) as? [String: Any],
              attributes[kSecAttrKeyType as String] as? String == kSecAttrKeyTypeECSECPrimeRandom as String,
              attributes[kSecAttrKeySizeInBits as String] as? Int == 256,
              let point = SecKeyCopyExternalRepresentation(key, nil) as Data?,
              point.count == 65, point.first == 0x04 else {
            return false
        }

        // The desktop hashes DER SubjectPublicKeyInfo. Security exports the uncompressed P-256 point.
        let p256Header: [UInt8] = [
            0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01,
            0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00
        ]
        let digest = SHA256.hash(data: Data(p256Header) + point).map { String(format: "%02x", $0) }.joined()
        guard digest == fingerprint else { return false }

        // An invite identifies the PC by key, independently of its changing IP address or hostname.
        return SecTrustSetPolicies(trust, SecPolicyCreateBasicX509()) == errSecSuccess
            && SecTrustSetAnchorCertificates(trust, [leaf] as CFArray) == errSecSuccess
            && SecTrustSetAnchorCertificatesOnly(trust, true) == errSecSuccess
            && SecTrustEvaluateWithError(trust, nil)
    }
}
