import Foundation

/// A point in whole pixels: a display's (origin at its top-left corner, y
/// down), as the protocol carries the cursor (docs/MACOS.md T18).
public struct PixelPoint: Sendable, Hashable, CustomStringConvertible {
    public let x: Int
    public let y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }

    public var json: JSONValue { ["x": .integer(Int64(x)), "y": .integer(Int64(y))] }
    public var description: String { "(\(x), \(y))" }
}

/// A rectangle in whole pixels of a display (origin at its top-left
/// corner, y down): the protocol's captureBounds.
public struct PixelRect: Sendable, Hashable, CustomStringConvertible {
    public let x: Int
    public let y: Int
    public let width: Int
    public let height: Int

    public init(x: Int, y: Int, width: Int, height: Int) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    public var size: PixelSize { PixelSize(width: width, height: height) }

    public var json: JSONValue {
        ["x": .integer(Int64(x)), "y": .integer(Int64(y)), "width": .integer(Int64(width)), "height": .integer(Int64(height))]
    }

    /// The protocol's captureBounds, or nil when a member is missing or not an integer.
    public init?(json value: JSONValue?) {
        guard let x = value?["x"]?.int64Value, let y = value?["y"]?.int64Value,
              let width = value?["width"]?.int64Value, let height = value?["height"]?.int64Value,
              let ix = Int(exactly: x), let iy = Int(exactly: y), let iw = Int(exactly: width),
              let ih = Int(exactly: height) else { return nil }
        self.init(x: ix, y: iy, width: iw, height: ih)
    }

    public var description: String { "(\(x), \(y), \(width)x\(height))" }
}

/// A point in points (Cocoa global coordinates: origin at the bottom-left
/// corner of the primary display, y up), as NSEvent.mouseLocation gives it.
public struct GlobalPoint: Sendable, Hashable {
    public let x: Double
    public let y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }
}

/// A rectangle in points. In a DisplayDescription it is Cocoa global
/// (NSScreen.frame: origin at the bottom-left corner, y up); from
/// CaptureGeometry.sourceRect it is in the display's own points (origin at
/// its top-left corner, y down), the screen capture's sourceRect.
public struct PointRect: Sendable, Hashable, CustomStringConvertible {
    public let x: Double
    public let y: Double
    public let width: Double
    public let height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    public var minX: Double { x }
    public var maxX: Double { x + width }
    public var minY: Double { y }
    public var maxY: Double { y + height }

    public var description: String { "(\(x), \(y), \(width)x\(height))" }
}

/// One display as the app describes it from NSScreen (id from
/// NSScreenNumber, the CGDirectDisplayID; frame from NSScreen.frame; pixel
/// size from its display mode's pixelWidth and pixelHeight), so the
/// geometry below needs no AppKit.
public struct DisplayDescription: Sendable, Hashable, CustomStringConvertible {
    public let id: UInt32
    public let frame: PointRect
    public let pixelWidth: Int
    public let pixelHeight: Int

    /// Nil when the frame is not finite and positive (its far edges
    /// included), the pixel size is outside CoreHost's 1..32768, or the
    /// pixels per point on an axis is not finite (a side so small that the
    /// division overflows): such a display cannot be planned for, and
    /// locate's arithmetic would meet 0 * infinity.
    public init?(id: UInt32, frame: PointRect, pixelWidth: Int, pixelHeight: Int) {
        guard frame.x.isFinite, frame.y.isFinite, frame.width.isFinite, frame.height.isFinite,
              frame.width > 0, frame.height > 0, frame.maxX.isFinite, frame.maxY.isFinite,
              (1...Frame.maxSide).contains(pixelWidth), (1...Frame.maxSide).contains(pixelHeight),
              (Double(pixelWidth) / frame.width).isFinite, (Double(pixelHeight) / frame.height).isFinite
        else { return nil }
        self.id = id
        self.frame = frame
        self.pixelWidth = pixelWidth
        self.pixelHeight = pixelHeight
    }

    /// Backing pixels per point on each axis (2 on Retina, 1 otherwise).
    public var pixelsPerPointX: Double { Double(pixelWidth) / frame.width }
    public var pixelsPerPointY: Double { Double(pixelHeight) / frame.height }

    /// The protocol's display: its id and its size in its own pixels.
    public var wire: ObservedDisplay { ObservedDisplay(id: id, width: pixelWidth, height: pixelHeight) }

    public var description: String { "display \(id) \(frame) \(pixelWidth)x\(pixelHeight) px" }
}

/// The display under the cursor and the cursor in its pixels.
public struct CursorLocation: Sendable, Hashable {
    public let display: DisplayDescription
    public let cursor: PixelPoint

    public init(display: DisplayDescription, cursor: PixelPoint) {
        self.display = display
        self.cursor = cursor
    }
}

/// From Cocoa's global points to the protocol's display pixels and back to
/// the screen capture's points (docs/MACOS.md T18, T21). Pure, so every
/// arrangement of displays is tested without a window server.
public enum CaptureGeometry {
    /// The display under `point` and `point` in that display's whole pixels.
    ///
    /// A display holds the points with minX <= x < maxX and minY < y <= maxY:
    /// Cocoa's global y is the primary display's height minus Quartz's y, so
    /// the top pixel row of a display reads y == maxY, and y == minY is the
    /// top row of the display below it. The first display in `displays`
    /// that holds the point wins (NSScreen.screens lists the primary first).
    ///
    /// Rounding: the cursor is the pixel that holds the point, as Windows'
    /// GetCursorPos gives it: x = floor((x - minX) * pixelsPerPointX), y =
    /// floor((maxY - y) * pixelsPerPointY), then clamped to the display's
    /// pixels, because CoreHost refuses a cursor at or past its width or
    /// height. Nil when no display holds the point or a coordinate is not
    /// finite: no hover then.
    public static func locate(_ point: GlobalPoint, on displays: [DisplayDescription]) -> CursorLocation? {
        guard point.x.isFinite, point.y.isFinite else { return nil }
        guard let display = displays.first(where: {
            point.x >= $0.frame.minX && point.x < $0.frame.maxX && point.y > $0.frame.minY && point.y <= $0.frame.maxY
        }) else { return nil }
        guard let x = pixel((point.x - display.frame.minX) * display.pixelsPerPointX, count: display.pixelWidth),
              let y = pixel((display.frame.maxY - point.y) * display.pixelsPerPointY, count: display.pixelHeight)
        else { return nil }
        return CursorLocation(display: display, cursor: PixelPoint(x: x, y: y))
    }

    /// The whole pixel that holds `offset` pixels from a display's edge,
    /// clamped into 0..<count; nil for NaN or no pixels. Clamped as a double
    /// and converted with Int(exactly:), which returns nil where Int(_:)
    /// traps (NaN, out of range), so no input traps here, even one a
    /// regressed caller lets through (a trap leaves a crash report).
    static func pixel(_ offset: Double, count: Int) -> Int? {
        guard count >= 1, !offset.isNaN else { return nil }
        return Int(exactly: min(max(offset.rounded(.down), 0), Double(count - 1)))
    }

    /// Capture bounds in display pixels as the screen capture's sourceRect:
    /// the display's own points, origin at its top-left corner, y down.
    ///
    /// Rounding: none. Each edge is divided by the display's pixels per
    /// point exactly, so an odd pixel edge on a 2x display is a half point;
    /// the capture's output size is set in pixels (bounds.width x
    /// bounds.height), which keeps the frame equal to the bounds 1:1.
    public static func sourceRect(for bounds: PixelRect, on display: DisplayDescription) -> PointRect {
        PointRect(x: Double(bounds.x) / display.pixelsPerPointX, y: Double(bounds.y) / display.pixelsPerPointY,
                  width: Double(bounds.width) / display.pixelsPerPointX,
                  height: Double(bounds.height) / display.pixelsPerPointY)
    }

    /// Whether `bounds` is at least 1x1 and lies wholly inside the display,
    /// as CoreHost requires of captureBounds. Subtracted, never added: a
    /// plan's x, y, width and height are any Int64 on the wire, and x +
    /// width would trap on overflow; with width >= 1 and the display's
    /// 1..32768 pixels, pixelWidth - width cannot overflow.
    public static func isInside(_ bounds: PixelRect, display: DisplayDescription) -> Bool {
        bounds.width >= 1 && bounds.height >= 1 && bounds.x >= 0 && bounds.y >= 0
            && bounds.x <= display.pixelWidth - bounds.width && bounds.y <= display.pixelHeight - bounds.height
    }
}
