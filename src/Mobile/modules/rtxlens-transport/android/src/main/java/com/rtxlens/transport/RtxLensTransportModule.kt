package com.rtxlens.transport

import expo.modules.kotlin.Promise
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition
import java.io.IOException
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

class RtxLensTransportModule : Module() {
    private val stream = AtomicReference<Stream?>()

    override fun definition() = ModuleDefinition {
        Name("RtxLensTransport")
        Events("onStreamState", "onFrame")

        AsyncFunction("post") { address: String, pin: String, path: String, json: String, promise: Promise ->
            post(address, pin, path, json, promise)
        }

        Function("openStream") { id: String, address: String, pin: String, token: String ->
            openStream(id, address, pin, token)
        }

        Function("closeStream") { id: String ->
            val current = stream.get()
            if (current?.id == id && stream.compareAndSet(current, null)) {
                current.socket?.cancel()
            }
        }

        OnDestroy {
            stream.getAndSet(null)?.socket?.cancel()
        }
    }

    private fun post(address: String, pin: String, path: String, json: String, promise: Promise) {
        try {
            require(path == "/pairing" || path == "/pairing/complete")
            require(json.toByteArray(Charsets.UTF_8).size <= 1024)

            val endpoint = readEndpoint(address)
            val client = createClient(endpoint, pin)
            val request = Request.Builder()
                .url(endpoint.newBuilder().encodedPath(path).build())
                .post(json.toRequestBody("application/json".toMediaType()))
                .build()

            client.newCall(request).enqueue(object : Callback {
                override fun onFailure(call: Call, error: IOException) {
                    promise.reject("PAIRING_FAILED", connectionError, error)
                }

                override fun onResponse(call: Call, response: Response) {
                    response.use {
                        try {
                            val source = response.body?.source()
                            if (source?.request(4097) == true) {
                                throw IOException("Pairing response is too large.")
                            }

                            promise.resolve(mapOf("status" to response.code, "body" to (source?.readUtf8() ?: "")))
                        } catch (error: IOException) {
                            promise.reject("PAIRING_FAILED", "The PC sent an invalid pairing response.", error)
                        }
                    }
                }
            })
        } catch (error: Exception) {
            promise.reject("PAIRING_FAILED", "The paired PC address or public key is invalid.", error)
        }
    }

    private fun openStream(id: String, address: String, pin: String, token: String) {
        stream.getAndSet(null)?.socket?.cancel()

        try {
            require(token.matches(Regex("[a-fA-F0-9]{32}\\.[A-Za-z0-9_-]{43}")))

            val endpoint = readEndpoint(address)
            val client = createClient(endpoint, pin)
            val request = Request.Builder()
                .url(endpoint.newBuilder().encodedPath("/sensors").build())
                .header("Authorization", "Bearer $token")
                .build()

            val current = Stream(id)
            stream.set(current)
            val socket = client.newWebSocket(request, object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    if (stream.get() === current) {
                        emitState(id, "connected")
                    }
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    if (stream.get() === current && text.length <= 1024 * 1024) {
                        sendEvent("onFrame", mapOf("id" to id, "json" to text))
                    }
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(1000, null)
                    if (stream.compareAndSet(current, null)) {
                        emitState(id, "closed")
                    }
                }

                override fun onFailure(webSocket: WebSocket, error: Throwable, response: Response?) {
                    val message = if (response?.code == 401) {
                        "The PC no longer authorizes this phone. Pair again in Mobile Access."
                    } else {
                        connectionError
                    }
                    response?.close()

                    if (stream.compareAndSet(current, null)) {
                        emitState(id, "error", message)
                    }
                }
            })

            current.socket = socket
            if (stream.get() !== current) {
                socket.cancel()
            }
        } catch (error: Exception) {
            stream.getAndSet(null)?.socket?.cancel()
            emitState(id, "error", "The paired PC address or public key is invalid.")
        }
    }

    private fun emitState(id: String, state: String, message: String? = null) {
        val event = mutableMapOf("id" to id, "state" to state)
        if (message != null) {
            event["message"] = message
        }
        sendEvent("onStreamState", event)
    }

    private fun readEndpoint(address: String): HttpUrl {
        val endpoint = requireNotNull(address.toHttpUrlOrNull())
        require(endpoint.isHttps && endpoint.port in 1024..65535 && endpoint.encodedPath == "/")
        require(endpoint.username.isEmpty() && endpoint.password.isEmpty() && endpoint.query == null && endpoint.fragment == null)
        return endpoint
    }

    private fun createClient(endpoint: HttpUrl, fingerprint: String): OkHttpClient {
        require(fingerprint.matches(Regex("[a-fA-F0-9]{64}")))
        val expected = fingerprint.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        val trust = object : X509TrustManager {
            override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
                val leaf = chain.firstOrNull() ?: throw CertificateException("The PC did not present a certificate.")
                leaf.checkValidity()
                val actual = MessageDigest.getInstance("SHA-256").digest(leaf.publicKey.encoded)
                if (!MessageDigest.isEqual(expected, actual)) {
                    throw CertificateException("The PC key does not match the pairing code.")
                }
            }

            override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) {
                throw CertificateException("Client certificates are not accepted.")
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }

        val tls = SSLContext.getInstance("TLS")
        tls.init(null, arrayOf(trust), null)

        return OkHttpClient.Builder()
            .sslSocketFactory(tls.socketFactory, trust)
            .hostnameVerifier { host, _ -> host == endpoint.host }
            .followRedirects(false)
            .followSslRedirects(false)
            .retryOnConnectionFailure(false)
            .callTimeout(12, TimeUnit.SECONDS)
            .build()
    }

    private class Stream(val id: String) {
        @Volatile var socket: WebSocket? = null
    }

    companion object {
        private const val connectionError = "Could not connect securely to the PC. Check its address and pairing key."
    }
}
