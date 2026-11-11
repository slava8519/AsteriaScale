import Foundation
import Testing
@testable import AsteriaScaleKit

@Suite struct StrictJSONTests {
    @Test func parsesEveryKindAndKeepsIntegersApart() throws {
        let value = try StrictJSON.parse(Data(#"{"a":[1,-2,0.5,1e3,true,false,null],"b":"Пр\u0438вет \ud83d\ude00\n"}"#.utf8))
        #expect(value["a"] == [.integer(1), .integer(-2), .number(0.5), .number(1000), true, false, nil])
        #expect(value["b"] == .string("Привет 😀\n"))
        #expect(try StrictJSON.parse(Data("17".utf8)) == .integer(17))
        #expect(try StrictJSON.parse(Data("17.0".utf8)) == .number(17))
    }

    @Test(arguments: [
        "", "{", "{\"a\":1,}", "[1,]", "{\"a\":1}x", "01", "1.", "-", "+1", "\"\\x\"", "\"\\ud800\"", "\"\\udc00\"",
        "{\"a\":1,\"a\":2}", "{\"a\":1}\r", "\u{FEFF}{}", "'a'", "\"a\u{01}\"", "nul", "[1 2]", "NaN",
    ])
    func rejects(_ text: String) {
        #expect(throws: JSONParseFailure.self) { _ = try StrictJSON.parse(Data(text.utf8)) }
    }

    @Test func rejectsInvalidUTF8AndDeepNesting() {
        for bytes in [[0x22, 0xC3, 0x28, 0x22], [0x22, 0xED, 0xA0, 0x80, 0x22], [0x22, 0xC0, 0xAF, 0x22],
                      [0x22, 0xF4, 0x90, 0x80, 0x80, 0x22]] as [[UInt8]] {
            #expect(throws: JSONParseFailure.self) { _ = try StrictJSON.parse(Data(bytes)) }
        }
        let deep = String(repeating: "[", count: StrictJSON.maxDepth + 2) + String(repeating: "]", count: StrictJSON.maxDepth + 2)
        #expect(throws: JSONParseFailure.self) { _ = try StrictJSON.parse(Data(deep.utf8)) }
    }

    @Test func serializesSortedRawUTF8AndRoundTrips() throws {
        let value: JSONValue = ["z": "Привет/мир", "a": ["x\n\u{01}\"\\", 3, 2.5], "m": nil, "t": true]
        let text = String(decoding: try value.serialized(), as: UTF8.self)
        #expect(text == #"{"a":["x\n\u0001\"\\",3,2.5],"m":null,"t":true,"z":"Привет/мир"}"#)
        #expect(try StrictJSON.parse(Data(text.utf8)) == value)
        #expect(throws: JSONSerializationFailure.self) { _ = try JSONValue.number(.infinity).serialized() }
    }
}

@Suite struct EnvelopeTests {
    @Test func framerSplitsAndKeepsAPartialLine() throws {
        var framer = LineFramer(maxLineBytes: 64)
        #expect(try framer.append(Data("{\"a\":1}\n{\"b\"".utf8)) == [Data("{\"a\":1}".utf8)])
        #expect(framer.pendingBytes == 4)
        #expect(try framer.append(Data(":2}\n".utf8)) == [Data("{\"b\":2}".utf8)])
        #expect(CoreProtocol.maxInboundLineBytes == 4 << 20)
    }

    @Test func framerRejectsAnOverlongLineEvenWithoutNewline() {
        var framer = LineFramer(maxLineBytes: 8)
        #expect(throws: ProtocolViolation.self) { _ = try framer.append(Data(repeating: 0x41, count: 9)) }
    }

    @Test func parsesTerminalsAndEvents() throws {
        #expect(try CoreLineParser.parse(Data(#"{"version":1,"id":7,"ok":true,"result":{"x":1}}"#.utf8))
                == .terminal(id: 7, result: .success(["x": 1])))
        #expect(try CoreLineParser.parse(Data(#"{"version":1,"event":"hover.primary","ref":7,"payload":{}}"#.utf8))
                == .event(CoreEvent(name: "hover.primary", ref: 7, payload: [:])))
        #expect(try CoreLineParser.parse(Data(#"{"version":1,"event":"status","payload":{"text":"т"}}"#.utf8))
                == .event(CoreEvent(name: "status", ref: nil, payload: ["text": "т"])))
        let superseded = try CoreLineParser.parse(Data(
            #"{"version":1,"id":17,"ok":false,"error":{"code":"superseded","message":"Запрос заменён новым наведением.","supersededBy":18}}"#.utf8))
        guard case .terminal(17, .failure(let error)) = superseded else {
            Issue.record("expected an error terminal, got \(superseded)")
            return
        }
        #expect(error.code == "superseded" && error.supersededBy == 18 && error.detail == nil)
    }

    @Test(arguments: [
        "", "not json", "[1]", #"{"id":1,"ok":true,"result":{}}"#, #"{"version":2,"id":1,"ok":true,"result":{}}"#,
        #"{"version":1.0,"id":1,"ok":true,"result":{}}"#, #"{"version":1,"id":1}"#,
        #"{"version":1,"id":0,"ok":true,"result":{}}"#, #"{"version":1,"id":"1","ok":true,"result":{}}"#,
        #"{"version":1,"id":1,"ok":true}"#, #"{"version":1,"id":1,"ok":true,"result":[]}"#,
        #"{"version":1,"id":1,"ok":true,"result":{},"error":{"code":"x","message":"м"}}"#,
        #"{"version":1,"id":1,"ok":false}"#, #"{"version":1,"id":1,"ok":false,"error":{"message":"м"}}"#,
        #"{"version":1,"id":1,"ok":false,"error":{"code":"x"}}"#, #"{"version":1,"id":1,"ok":true,"result":{},"extra":1}"#,
        #"{"version":1,"event":"x","ref":"7","payload":{}}"#, #"{"version":1,"event":"x","ref":0,"payload":{}}"#,
        #"{"version":1,"event":"x"}"#, #"{"version":1,"event":"","payload":{}}"#,
        #"{"version":1,"event":"x","payload":{},"id":1}"#, #"{"version":1,"event":"x","payload":[]}"#,
    ])
    func violations(_ line: String) {
        #expect(throws: ProtocolViolation.self) { _ = try CoreLineParser.parse(Data(line.utf8)) }
    }

    @Test func requestLinesFollowTheGoldenEnvelope() throws {
        let bare = String(decoding: try CoreRequestLine(id: 1, command: "core.ping").encodedLine(), as: UTF8.self)
        #expect(bare == "{\"version\":1,\"id\":1,\"command\":\"core.ping\"}\n")
        let line = String(decoding: try CoreRequestLine(id: 5, command: "hover.resolve",
                                                         payload: ["text": "Привет/мир"]).encodedLine(), as: UTF8.self)
        #expect(line == "{\"version\":1,\"id\":5,\"command\":\"hover.resolve\",\"payload\":{\"text\":\"Привет/мир\"}}\n")
        #expect(throws: JSONSerializationFailure.self) {
            _ = try CoreRequestLine(id: 6, command: "x", payload: [1]).encodedLine()
        }
    }
}

/// tests/AsteriaScale.Tests.MacOS/CoreHost/protocol-v1.golden.jsonl, the
/// wire bytes the C# conformance check replays against CoreHost.
@Suite struct GoldenTranscriptTests {
    static func lines() throws -> [Data] {
        try Data(contentsOf: Repo.golden).split(separator: 0x0A).map { Data($0) }
    }

    @Test func everyOutboundLineDecodes() throws {
        var terminals: [Int64: Result<JSONValue, CoreError>] = [:]
        var events: [CoreEvent] = []
        var requests = 0
        for line in try Self.lines() {
            let frame = try StrictJSON.parse(line)
            if frame["command"] != nil {
                requests += 1
                continue
            }
            switch try CoreLineParser.parse(line) {
            case .terminal(let id, let result):
                #expect(terminals[id] == nil, "a second terminal for #\(id)")
                terminals[id] = result
            case .event(let event): events.append(event)
            }
        }
        #expect(requests == 22 && terminals.count == 22, "one terminal per request")
        #expect(events.map(\.name) == ["hover.primary", "hover.phrase", "hover.phrase", "status"])

        let hello = CoreHelloInfo(try #require(try? terminals[2]?.get()))
        #expect(hello.protocolVersion == 1 && hello.pid == 4242 && hello.pgid == 4242)
        #expect(hello.processGroupLeader == true && hello.testCommands == true && hello.blockedSignals == [])
        #expect(HelloCheck.problems(hello, spawnedPid: 4242, expectedBuild: "golden", allowTestCommands: true).isEmpty)
        #expect(HelloCheck.problems(hello, spawnedPid: 4242, expectedBuild: "golden", allowTestCommands: false)
                == ["test commands are enabled"])
        #expect(hello.raw["capabilities"]?["hoverSources"] == ["selection", "observations"])

        // ocr.select answers before core.init with Core's selection: the
        // character under the cursor (16), not the word's start (14).
        let select = try #require(try? terminals[5]?.get())
        #expect(select["selection"]?["text"] == "He sat on the bank of the river.")
        #expect(select["selection"]?["hoveredWord"] == "bank" && select["selection"]?["characterIndex"] == 16)
        #expect(select["selection"]?["sentenceStable"] == true && select["selection"]?["speakerName"] == nil)
        #expect(select["lines"] == ["He sat on the bank of the river."])
        guard case .failure(let observationsHover)? = terminals[9] else { Issue.record("#9 must fail"); return }
        #expect(observationsHover.code == "not_ready")

        let primary = HoverPrimary(hoverId: 18, payload: events[0].payload)
        #expect(events[0].ref == 18 && primary.source == "river" && primary.found)
        #expect(primary.provenance == .dictionary && primary.badge == "СЛОВАРЬ" && primary.engineLabel == "ПРОВЕРКА")
        let draft = HoverPhrase(hoverId: 18, payload: events[1].payload)
        let final = HoverPhrase(hoverId: 18, payload: events[2].payload)
        #expect(draft.provisional && !final.provisional && final.translation == "проверочная фраза")
        #expect(events[3].ref == nil && events[3].payload["state"] == "error")

        guard case .failure(let superseded)? = terminals[17] else { Issue.record("#17 must fail"); return }
        #expect(superseded.code == "superseded" && superseded.supersededBy == 18)
        guard case .failure(let helloRequired)? = terminals[1] else { Issue.record("#1 must fail"); return }
        #expect(helloRequired.code == "hello_required" && !helloRequired.message.lowercased().contains(hostWord))
        #expect(try terminals[18]?.get()["outcome"] == "completed")
        #expect(try terminals[20]?.get()["outcome"] == "failed")
    }

    /// The app's encoder writes each golden request with the same envelope
    /// bytes and an equal payload (payload keys come out sorted).
    @Test func requestsReencodeToTheGoldenEnvelope() throws {
        for line in try Self.lines() {
            let frame = try StrictJSON.parse(line)
            guard let command = frame["command"]?.stringValue else { continue }
            let id = try #require(frame["id"]?.int64Value)
            let encoded = try CoreRequestLine(id: id, command: command, payload: frame["payload"]).encodedLine()
            #expect(try StrictJSON.parse(encoded.dropLast()) == frame)
            let prefix = "{\"version\":1,\"id\":\(id),\"command\":\"\(command)\""
            #expect(String(decoding: line, as: UTF8.self).hasPrefix(prefix))
            #expect(String(decoding: encoded, as: UTF8.self).hasPrefix(prefix))
        }
    }
}

/// tests/AsteriaScale.Tests.MacOS/CoreHost/launch-args-v1.txt is the single
/// source of CoreHost's arguments; the C# conformance check reads it too.
@Suite struct LaunchArgumentTests {
    struct Line { let name: String; let kind: String; let value: String }

    static func golden() throws -> [Line] {
        try String(contentsOf: Repo.launchArguments, encoding: .utf8).split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && !$0.hasPrefix("#") }
            .map { line in
                let parts = line.split(separator: " ").map(String.init)
                return Line(name: parts[0], kind: parts[1], value: parts[2])
            }
    }

    @Test func launchArgumentsMatchGolden() throws {
        let golden = try Self.golden()
        #expect(golden.count == 10)
        let appFacing = golden.filter { $0.kind == "required" || $0.kind == "optional" }
        // Swift names exactly the required and optional arguments, each once,
        // and uses every required one always.
        #expect(Set(appFacing.map(\.name)) == Set(AppPaths.launchArgumentUse.keys))
        for line in appFacing where line.kind == "required" {
            #expect(AppPaths.launchArgumentUse[line.name] == .always, "\(line.name) is required")
        }
        for line in golden where line.kind == "test" || line.kind == "reserved" {
            #expect(AppPaths.launchArgumentUse[line.name] == nil, "\(line.name) is \(line.kind)")
        }

        let paths = AppPaths.bundled(bundle: URL(fileURLWithPath: "/Applications/Asteria Scale.app"),
                                     home: URL(fileURLWithPath: "/Users/someone"))
        for withWorker in [true, false] {
            let candidate = withWorker ? paths
                : AppPaths(home: paths.home, coreHost: paths.coreHost, dataRoot: paths.dataRoot, rankerWorker: nil)
            let arguments = candidate.coreHostArguments()
            var seen: [String] = []
            var index = 0
            while index < arguments.count {
                let name = arguments[index]
                let line = try #require(golden.first { $0.name == name }, "unknown argument \(name)")
                #expect(line.kind == "required" || line.kind == "optional", "\(name) is \(line.kind)")
                #expect(!seen.contains(name), "\(name) twice")
                seen.append(name)
                if line.value == "path" {
                    let value = arguments[index + 1]
                    #expect(value.hasPrefix("/") && !value.contains("/../"), "\(name) \(value) must be absolute")
                    index += 2
                } else {
                    index += 1
                }
            }
            for line in golden where line.kind == "required" { #expect(seen.contains(line.name)) }
            #expect(seen.contains("--ranker-worker") == withWorker && seen.contains("--ranker-models") == withWorker,
                    "the ranker pair goes together")
        }
    }
}
