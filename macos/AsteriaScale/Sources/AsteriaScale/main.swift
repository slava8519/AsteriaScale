import AppKit
import AsteriaScaleKit
import Darwin

// A write to a dead CoreHost must fail with EPIPE, never kill the menu-bar
// app. CoreConnection also sets F_SETNOSIGPIPE on its pipe; this covers any
// other descriptor. ChildProcess resets SIGPIPE to the default for helpers.
signal(SIGPIPE, SIG_IGN)

/// Command-line options. LaunchServices may add -psn_… and the like, so
/// unknown arguments are ignored rather than refused.
struct LaunchOptions {
    /// tools/macos/build-app.sh --self-test: run the scripted checks, print
    /// `selftest …` lines and exit 0 or 1. Only in a throwaway home.
    var selfTest = false
    /// The worker's model folder instead of
    /// ~/Library/Application Support/AsteriaScale/Models/Ranker: development
    /// runs read the repository's src/AsteriaScale.RankerWorker/Models in
    /// place (docs/MACOS.md T9), never a copy.
    var rankerModels: URL?

    static func parse(_ arguments: [String]) -> LaunchOptions {
        var options = LaunchOptions()
        var index = 1
        while index < arguments.count {
            switch arguments[index] {
            case "--self-test":
                options.selfTest = true
            case "--ranker-models" where index + 1 < arguments.count:
                index += 1
                options.rankerModels = URL(fileURLWithPath: arguments[index], isDirectory: true).standardizedFileURL
            default:
                break
            }
            index += 1
        }
        return options
    }
}

let app = NSApplication.shared
let delegate = AppDelegate(options: LaunchOptions.parse(CommandLine.arguments))
app.delegate = delegate
// LSUIElement already makes the bundle an accessory app (no Dock icon); this
// also covers the bare binary.
app.setActivationPolicy(.accessory)

// SIGTERM (launchd at logout, `kill`) and SIGINT (^C of an isolated run in a
// terminal) take the same orderly path as «Выход»: CoreHost gets
// core.shutdown instead of being orphaned. The default action would end the
// app at once; CoreHost would then still see stdin EOF and empty its group.
//
// terminate(_:) must not run inside a main-queue block: with .terminateLater
// it spins a nested run loop until the reply, and that loop cannot drain the
// main queue it is called from, so the shutdown task (main actor) would never
// run. The signal source therefore hands the call to the run loop itself.
var terminationSources: [DispatchSourceSignal] = []
for number in [SIGTERM, SIGINT] {
    signal(number, SIG_IGN)
    let source = DispatchSource.makeSignalSource(signal: number, queue: .main)
    source.setEventHandler {
        CFRunLoopPerformBlock(CFRunLoopGetMain(), CFRunLoopMode.commonModes.rawValue) {
            MainActor.assumeIsolated { delegate.terminateFromSignal(number) }
        }
        CFRunLoopWakeUp(CFRunLoopGetMain())
    }
    source.resume()
    terminationSources.append(source)
}

app.run()
