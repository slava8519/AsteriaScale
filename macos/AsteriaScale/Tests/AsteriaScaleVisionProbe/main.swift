import AsteriaScaleKit
import AsteriaScaleVision
import Darwin
import Foundation

// The DoD 6 probe of roadmap 20.3 (docs/DEVELOPMENT.md, «Проба Vision»): does
// the real Apple Vision, as AsteriaScaleVision sets it up, recognize the
// Latin fixture in a binary signed ad hoc with the hardened runtime and no
// entitlements, in a throwaway home, without a crash report? Built beside
// the test bundle by tools/macos/test-swift.sh; never part of the app and
// never run by the suite (docs/MACOS.md T26). Run only a re-signed copy by
// direct exec with HOME, CFFIXED_USER_HOME, TMPDIR and cwd in a throwaway
// folder: the first recognition compiles Vision's models into that home.
//
// Prints the configuration, the timings, every recognized line and word box
// against the rendered one, and exits 0 when every check passed, 1 when one
// failed. It has no trap path (a crash would leave a report in the owner's
// DiagnosticReports): every failure is printed and ends in exit(1).

func say(_ line: String) {
    print(line)
    fflush(stdout)
}

func milliseconds(_ duration: Duration) -> String {
    let parts = duration.components
    return String(format: "%.0f ms", Double(parts.seconds) * 1000 + Double(parts.attoseconds) / 1e15)
}

func probe() async -> Bool {
    var ok = true
    let environment = ProcessInfo.processInfo.environment
    say("probe process \(ProcessInfo.processInfo.processName) pid \(getpid())")
    say("probe HOME \(environment["HOME"] ?? "unset") CFFIXED_USER_HOME \(environment["CFFIXED_USER_HOME"] ?? "unset")")
    say("probe TMPDIR \(environment["TMPDIR"] ?? "unset") cwd \(FileManager.default.currentDirectoryPath)")

    let configuration = VisionTextRecognizer.configuration
    let problems = configuration.problems()
    say("probe vision.config: \(problems.isEmpty ? "ok" : "FAIL") — \(configuration); supported revisions "
        + "\(VisionTextRecognizer.supportedRevisions)" + (problems.isEmpty ? "" : "; \(problems.joined(separator: "; "))"))
    ok = ok && problems.isEmpty

    let recognizer = VisionTextRecognizer()
    let warmStart = ContinuousClock.now
    do throws(RecognitionFailure) {
        let lines = try await VisionFixture.warmUp(recognizer)
        let took = warmStart.duration(to: .now)
        let read = lines.map(\.text)
        let warmOk = read == [VisionFixture.warmUpLine]
        say("probe vision.warmup: \(warmOk ? "ok" : "FAIL") — first recognition in this process \(milliseconds(took)), read \(read)")
        ok = ok && warmOk
    } catch {
        say("probe vision.warmup: FAIL — \(error.code): \(error)")
        return false
    }

    for density in [2.0, 1.0] {
        let fixture: RenderedFixture
        do throws(RecognitionFailure) {
            fixture = try VisionFixture.render(pixelsPerPoint: density)
        } catch {
            say("probe fixture \(Int(density))x: FAIL — \(error)")
            ok = false
            continue
        }
        let started = ContinuousClock.now
        let recognition: VisionRecognition
        do throws(RecognitionFailure) {
            recognition = try await recognizer.recognition(fixture.frame, capturedAt: fixture.bounds)
        } catch {
            say("probe fixture \(Int(density))x: FAIL — \(error.code): \(error)")
            ok = false
            continue
        }
        let took = started.duration(to: .now)
        let recognized = recognition.lines
        let comparison = VisionFixture.compare(recognized, with: fixture.layout)
        // The size of the image Vision was given, measured: the frame-pixel
        // boxes cannot show whether a 1x frame was enlarged (T24).
        let seenProblem = VisionFixture.seenProblem(recognition.seenSize, for: fixture.frame)
        let fixtureOk = comparison.ok && seenProblem == nil
        say("probe fixture \(Int(density))x: \(fixtureOk ? "ok" : "FAIL") — frame \(fixture.frame.size), "
            + "Vision given \(recognition.seenSize), \(milliseconds(took)); \(comparison.summary)")
        for problem in comparison.problems + (seenProblem.map { [$0] } ?? []) { say("  problem: \(problem)") }
        for line in recognized.sorted(by: { ($0.box?.top ?? 0) < ($1.box?.top ?? 0) }) {
            say("  line «\(line.text)» confidence \(line.confidence) box \(line.box.map { "\($0)" } ?? "none")")
            // Word by word in order, as VisionFixture.compare pairs them.
            let rendered = fixture.layout.lines.first { $0.text == line.text }?.words ?? []
            for (index, word) in line.words.enumerated() {
                let expected = index < rendered.count ? rendered[index] : nil
                say("    word «\(word.text)» utf16 \(word.utf16Range) box \(word.box)"
                    + (expected.map { " rendered «\($0.text)» \($0.box)" } ?? " not rendered"))
            }
        }
        ok = ok && fixtureOk
    }

    let mismatch = PixelRect(x: 0, y: 0, width: 2, height: 2)
    do throws(RecognitionFailure) {
        let tiny = try Frame(width: 1, height: 1, pixels: Data(repeating: 255, count: 4), pixelsPerPoint: 2)
        _ = try await recognizer.recognize(tiny, capturedAt: mismatch)
        say("probe frameMismatch: FAIL — a 1x1 frame for 2x2 bounds was recognized")
        ok = false
    } catch {
        let refused = error == .frameMismatch(frame: PixelSize(width: 1, height: 1), bounds: mismatch.size)
        say("probe frameMismatch: \(refused ? "ok" : "FAIL") — \(error.code): \(error.russian)")
        ok = ok && refused
    }
    return ok
}

let started = ContinuousClock.now
let passed = await probe()
say("PROBE \(passed ? "PASS" : "FAIL") in \(milliseconds(started.duration(to: .now)))")
exit(passed ? 0 : 1)
