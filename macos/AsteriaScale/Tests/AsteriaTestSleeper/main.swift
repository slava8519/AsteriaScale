import Darwin

// A stand-in for sleep(1) that the suite can copy under its scratch folder
// (OrphanSweepTests.killsALeaderUnderALinkedPrefix needs an executable whose
// real path lies under /private). A copy of /bin/sleep cannot serve: outside
// the sealed system volume it is no longer a platform binary, the kernel
// marks its signature invalid, and its death, even by the test's own SIGKILL,
// makes ReportCrash file "SIGKILL (Code Signature Invalid)" in the owner's
// DiagnosticReports.
// This one is an arm64 SwiftPM product, linker-signed ad hoc, so a copy runs
// from any folder and a SIGKILL leaves no report.
//
// Like sleep(1) it takes a duration in seconds and exits 0 after it, so the
// suite's marker duration 3141.59 (tools/macos/test-swift.sh finds leftovers
// by "sleep 3141.59") works for a copy named sleep as well.
let arguments = CommandLine.arguments
let seconds = max(0, arguments.count > 1 ? Double(arguments[1]) ?? 0 : 0)
var request = timespec(tv_sec: Int(seconds), tv_nsec: Int((seconds - seconds.rounded(.down)) * 1_000_000_000))
var remaining = timespec()
while nanosleep(&request, &remaining) == -1, errno == EINTR {
    request = remaining
}
