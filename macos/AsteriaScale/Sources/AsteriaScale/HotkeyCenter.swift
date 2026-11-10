import AppKit
import AsteriaScaleKit
import Carbon.HIToolbox

/// Global hotkeys through Carbon's RegisterEventHotKey, which needs no
/// privacy permission (an NSEvent global monitor needs Accessibility, an
/// event tap Input Monitoring). One handler on the application target
/// receives every press; Carbon delivers application-target events on the
/// main thread.
@MainActor
final class HotkeyCenter {
    private var refs: [HotkeyAction: EventHotKeyRef] = [:]
    /// The gestures that are registered now.
    private(set) var registered: [HotkeyAction: HotkeySpec] = [:]
    /// RegisterEventHotKey's last status per action (0 is noErr).
    private(set) var statuses: [HotkeyAction: OSStatus] = [:]
    /// The Russian error of every action whose gesture could not be registered.
    private(set) var problems: [HotkeyAction: String] = [:]
    private(set) var handlerStatus: OSStatus = noErr
    private var handlerRef: EventHandlerRef?
    private let onPress: (HotkeyAction) -> Void

    init(onPress: @escaping (HotkeyAction) -> Void) {
        self.onPress = onPress
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        // The center lives as long as the app (AppDelegate owns it), so an
        // unretained pointer is safe for the handler's lifetime.
        let context = Unmanaged.passUnretained(self).toOpaque()
        handlerStatus = InstallEventHandler(GetApplicationEventTarget(), { _, event, context in
            guard let event, let context else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            let status = GetEventParameter(event, EventParamName(kEventParamDirectObject),
                                           EventParamType(typeEventHotKeyID), nil,
                                           MemoryLayout<EventHotKeyID>.size, nil, &id)
            guard status == noErr, id.signature == HotkeyAction.carbonSignature,
                  let action = HotkeyAction(carbonId: id.id) else { return OSStatus(eventNotHandledErr) }
            MainActor.assumeIsolated {
                Unmanaged<HotkeyCenter>.fromOpaque(context).takeUnretainedValue().onPress(action)
            }
            return noErr
        }, 1, &spec, context, &handlerRef)
    }

    /// Registers `spec` for `action`. A gesture the system refuses (taken by
    /// another app, or Option-only combinations if a macOS version rejects
    /// them, docs/MACOS.md M14) is never replaced by another one: the action
    /// stays without a hotkey and the Russian error is returned and kept in
    /// `problems` for the menu and the settings window.
    @discardableResult
    func register(_ action: HotkeyAction, _ spec: HotkeySpec) -> String? {
        if let old = refs.removeValue(forKey: action) { UnregisterEventHotKey(old) }
        registered[action] = nil
        problems[action] = nil
        var ref: EventHotKeyRef?
        let id = EventHotKeyID(signature: HotkeyAction.carbonSignature, id: action.carbonId)
        let status = RegisterEventHotKey(spec.keyCode, spec.modifiers.rawValue, id, GetApplicationEventTarget(), 0, &ref)
        statuses[action] = status
        guard status == noErr, let ref else {
            let problem = status == eventHotKeyExistsErr
                ? RU.hotkeyTaken(spec.display) : RU.hotkeyFailed(spec.display, status: status)
            problems[action] = problem
            return problem
        }
        refs[action] = ref
        registered[action] = spec
        return nil
    }

    func unregisterAll() {
        for (_, ref) in refs { UnregisterEventHotKey(ref) }
        refs.removeAll()
        registered.removeAll()
    }

    /// The self-test only: delivers a kEventHotKeyPressed event to our own
    /// handler in-process (SendEventToEventTarget needs no permission). It
    /// proves the handler and the id dispatch, not that the window server
    /// routes a physical key press; that stays a manual check.
    func synthesizePress(_ action: HotkeyAction) -> OSStatus {
        var event: EventRef?
        var status = CreateEvent(nil, OSType(kEventClassKeyboard), UInt32(kEventHotKeyPressed),
                                 GetCurrentEventTime(), EventAttributes(kEventAttributeNone), &event)
        guard status == noErr, let event else { return status }
        defer { ReleaseEvent(event) }
        var id = EventHotKeyID(signature: HotkeyAction.carbonSignature, id: action.carbonId)
        status = SetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                   MemoryLayout<EventHotKeyID>.size, &id)
        guard status == noErr else { return status }
        return SendEventToEventTarget(event, GetApplicationEventTarget())
    }
}
