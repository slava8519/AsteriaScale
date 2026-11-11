import Foundation

/// What sends a protocol v1 request: the supervisor in the app, one
/// connection in the tests. Both are actors, so a call is one await.
public protocol CoreRequesting: Sendable {
    func request(_ command: String, _ payload: JSONValue?, deadline: Duration, cancelInHost: Bool,
                 maxLineBytes: Int?, onSent: (@Sendable (Int64) -> Void)?,
                 onEvent: CoreConnection.EventHandler?) async throws -> JSONValue
}

extension CoreConnection: CoreRequesting {}
extension CoreSupervisor: CoreRequesting {}

/// The typed calls of Apple Vision observations (20.3): ocr.plan, ocr.select
/// and the observations hover. Every request that carries observations is
/// checked against CoreHost's limits and measured as the encoded line
/// against ObservationBudget first; one over either is refused here with a
/// Russian message (observations_too_large) and never truncated. The hover
/// orchestration around these calls (capture, recognition, the one retry on
/// stale_frame) comes with the screen capture.
public struct ObservationClient: Sendable {
    public let core: any CoreRequesting

    public init(core: any CoreRequesting) { self.core = core }

    /// ocr.plan: the capture bounds, mode and observation count of the next
    /// hover. Throws CoreError (not_ready before core.init, hover_disabled,
    /// bad_request, bad_response for an answer of another shape) or
    /// CancellationError.
    public func plan(display: ObservedDisplay, cursor: PixelPoint) async throws -> CapturePlan {
        let result = try await core.request("ocr.plan", Self.planPayload(display: display, cursor: cursor),
                                            deadline: CoreDeadlines.plan, cancelInHost: true, maxLineBytes: nil,
                                            onSent: nil, onEvent: nil)
        guard let plan = CapturePlan(result: result) else { throw Self.badResponse("ocr.plan", result) }
        return plan
    }

    /// ocr.select: Core's selection in the observations, without state,
    /// translation or history; needs core.hello only.
    public func select(mode: TranslationMode, source: ObservationsSource) async throws -> OcrSelectResult {
        if let problem = ObservationLimits.problem(source) {
            throw CoreError.observationsTooLarge("ocr.select", limit: problem)
        }
        let result = try await core.request("ocr.select", Self.selectPayload(mode: mode, source: source),
                                            deadline: CoreDeadlines.select, cancelInHost: true,
                                            maxLineBytes: ObservationBudget.maxLineBytes, onSent: nil, onEvent: nil)
        guard let selection = OcrSelectResult(result: result) else { throw Self.badResponse("ocr.select", result) }
        return selection
    }

    /// hover.resolve with an observations source. Updates arrive in
    /// CoreHost's order: hover.loading, hover.primary, then (from 20.5)
    /// hover.phrase. stale_frame ends it as `.staleFrame`.
    public func resolve(_ source: ObservationsSource, mode: TranslationMode, game: HoverGame? = nil,
                        onSent: (@Sendable (Int64) -> Void)? = nil,
                        onUpdate: @escaping @Sendable (HoverUpdate) -> Void) async -> HoverOutcome {
        if let problem = ObservationLimits.problem(source) {
            return .rejected(CoreError.observationsTooLarge("hover.resolve", limit: problem))
        }
        let hoverId = Locked<Int64>(0)
        do {
            let result = try await core.request(
                "hover.resolve", Self.hoverPayload(source, mode: mode, game: game), deadline: CoreDeadlines.hover,
                cancelInHost: true, maxLineBytes: ObservationBudget.maxLineBytes,
                onSent: { id in
                    hoverId.set(id)
                    onSent?(id)
                },
                onEvent: { event in
                    switch event.name {
                    case "hover.loading": onUpdate(.loading(HoverLoading(hoverId: hoverId.get, payload: event.payload)))
                    case "hover.primary": onUpdate(.primary(HoverPrimary(hoverId: hoverId.get, payload: event.payload)))
                    case "hover.phrase": onUpdate(.phrase(HoverPhrase(hoverId: hoverId.get, payload: event.payload)))
                    default: break
                    }
                })
            return HoverOutcome(result: result)
        } catch {
            return HoverOutcome(error: error)
        }
    }

    // The payloads, as protocol v1 defines them (Wire.cs; the golden lines).

    public static func planPayload(display: ObservedDisplay, cursor: PixelPoint) -> JSONValue {
        ["display": display.json, "cursor": cursor.json]
    }

    public static func selectPayload(mode: TranslationMode, source: ObservationsSource) -> JSONValue {
        ["mode": .string(mode.rawValue), "source": source.json]
    }

    public static func hoverPayload(_ source: ObservationsSource, mode: TranslationMode, game: HoverGame?) -> JSONValue {
        var payload: [String: JSONValue] = ["mode": .string(mode.rawValue), "source": source.json]
        if let game { payload["game"] = game.json }
        return .object(payload)
    }

    static func badResponse(_ command: String, _ result: JSONValue) -> CoreError {
        let text = (try? result.serialized()).map { String(decoding: $0.prefix(200), as: UTF8.self) } ?? "?"
        return CoreError(code: "bad_response", message: RU.errorText("bad_response"),
                         detail: "\(command) answered \(text)")
    }
}
