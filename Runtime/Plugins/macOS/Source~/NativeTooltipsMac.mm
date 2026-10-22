// NativeTooltipsMac — native AppKit tooltips for the Unity player on macOS.
//
// Build: ./build_macos.sh  (produces a universal NativeTooltipsMac.bundle, arm64 + x86_64)
//
// Problem: AppKit only shows a tooltip once the cursor ENTERS the tooltip rect. If the rect/text
// changes while the cursor is already over the view (which is always the case — Unity draws the
// whole UI into a single view), nothing appears.
//
// Solution (technique from WebKit's WebHTMLView / Chromium's RenderWidgetHostViewCocoa):
// our view captures the tracking rect that NSToolTipManager registers via
// -addTrackingRect:owner:userData:assumeInside:, and when the text changes it sends that owner
// fake mouseExited/mouseEntered events. The tooltip manager then measures the system delay
// itself and displays a real, system tooltip.
//
// Custom bubble (used when a TooltipStyle changes something AppKit tooltips can't do): a borderless,
// non-activating NSPanel at pop-up level whose background is an NSVisualEffectView with the system
// tooltip material (NSVisualEffectMaterialToolTip, macOS 10.14+), the system tooltip font
// (+[NSFont toolTipsFontOfSize:]) and label colour — so it looks like the real thing but can be
// resized, recoloured and placed anywhere. Placement is computed in C#.

#import <AppKit/AppKit.h>

#if !__has_feature(objc_arc)
#error "Compile with -fobjc-arc"
#endif

static const NSTrackingRectTag kNTTrackingRectTag = 0x4E54544C; // 'NTTL'

// Set to 1 if on some macOS version the tooltip doesn't appear over the transparent view.
// The view then accepts mouse hits and forwards them up the responder chain to Unity's view.
#ifndef NT_OVERLAY_ACCEPTS_HITS
#define NT_OVERLAY_ACCEPTS_HITS 0
#endif

@interface NTTooltipOverlayView : NSView
- (void)nt_setToolTipAtMousePoint:(NSString*)toolTip;
@end

@implementation NTTooltipOverlayView
{
    __unsafe_unretained id _trackingRectOwner; // owned by AppKit, not retained
    void* _trackingRectUserData;
    NSToolTipTag _lastToolTipTag;
    NSString* _toolTip;
}

- (instancetype)initWithFrame:(NSRect)frame
{
    if ((self = [super initWithFrame:frame]))
    {
        self.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    }
    return self;
}

#if NT_OVERLAY_ACCEPTS_HITS
- (BOOL)acceptsFirstMouse:(NSEvent*)event
{
    return [self.superview acceptsFirstMouse:event];
}
#else
- (NSView*)hitTest:(NSPoint)point
{
    return nil; // the mouse goes through to Unity's view
}
#endif

- (BOOL)isOpaque { return NO; }
- (BOOL)acceptsFirstResponder { return NO; }
- (void)drawRect:(NSRect)dirtyRect { }

// ---------------------------------------------------------------- capturing the tracking rect

- (NSTrackingRectTag)addTrackingRect:(NSRect)rect owner:(id)owner userData:(void*)data assumeInside:(BOOL)assumeInside
{
    _trackingRectOwner = owner;
    _trackingRectUserData = data;
    return kNTTrackingRectTag;
}

// Private variants AppKit uses on some OS versions.
- (NSTrackingRectTag)_addTrackingRect:(NSRect)rect owner:(id)owner userData:(void*)data assumeInside:(BOOL)assumeInside useTrackingNum:(int)tag
{
    _trackingRectOwner = owner;
    _trackingRectUserData = data;
    return kNTTrackingRectTag;
}

- (void)_addTrackingRects:(NSRect*)rects owner:(id)owner userDataList:(void**)userDataList assumeInsideList:(BOOL*)assumeInsideList trackingNums:(NSTrackingRectTag*)trackingNums count:(int)count
{
    if (count < 1) return;
    _trackingRectOwner = owner;
    _trackingRectUserData = userDataList[0];
    trackingNums[0] = kNTTrackingRectTag;
}

- (void)removeTrackingRect:(NSTrackingRectTag)tag
{
    if (tag == 0) return;
    if (tag == kNTTrackingRectTag)
    {
        _trackingRectOwner = nil;
        _trackingRectUserData = NULL;
        return;
    }
    [super removeTrackingRect:tag];
}

- (void)_removeTrackingRects:(NSTrackingRectTag*)tags count:(int)count
{
    for (int i = 0; i < count; ++i)
    {
        if (tags[i] == kNTTrackingRectTag)
        {
            _trackingRectOwner = nil;
            _trackingRectUserData = NULL;
        }
    }
}

// ---------------------------------------------------------------- fake enter/exit

- (NSEvent*)nt_fakeEvent:(NSEventType)type
{
    // Only the window, tracking number and userData matter.
    return [NSEvent enterExitEventWithType:type
                                  location:NSZeroPoint
                             modifierFlags:0
                                 timestamp:0
                              windowNumber:self.window.windowNumber
                                   context:nil
                               eventNumber:0
                            trackingNumber:kNTTrackingRectTag
                                  userData:_trackingRectUserData];
}

- (void)nt_sendToolTipMouseExited
{
    if (_trackingRectOwner == nil) return;
    [_trackingRectOwner mouseExited:[self nt_fakeEvent:NSEventTypeMouseExited]];
}

- (void)nt_sendToolTipMouseEntered
{
    if (_trackingRectOwner == nil) return;
    [_trackingRectOwner mouseEntered:[self nt_fakeEvent:NSEventTypeMouseEntered]];
}

- (void)nt_setToolTipAtMousePoint:(NSString*)string
{
    NSString* toolTip = string.length == 0 ? nil : string;
    if (toolTip == _toolTip || (toolTip != nil && _toolTip != nil && [toolTip isEqualToString:_toolTip]))
        return;

    if (_toolTip != nil)
        [self nt_sendToolTipMouseExited];

    _toolTip = [toolTip copy];

    if (_toolTip != nil)
    {
        // Remove all of them (not just ours) — AppKit can leave stale rects behind.
        [self removeAllToolTips];
        NSRect wideOpen = NSMakeRect(-100000, -100000, 200000, 200000);
        _lastToolTipTag = [self addToolTipRect:wideOpen owner:self userData:NULL];
        [self nt_sendToolTipMouseEntered];
    }
}

// NSToolTipOwner (informal protocol)
- (NSString*)view:(NSView*)view stringForToolTip:(NSToolTipTag)tag point:(NSPoint)point userData:(void*)data
{
    return [_toolTip copy];
}

@end

// ---------------------------------------------------------------- C API for Unity

static NTTooltipOverlayView* gOverlay = nil;

static NSWindow* NTFindUnityWindow(void)
{
    NSWindow* w = NSApp.mainWindow;
    if (w == nil) w = NSApp.keyWindow;
    if (w != nil && w.contentView != nil) return w;

    for (NSWindow* candidate in NSApp.windows)
        if (candidate.isVisible && candidate.contentView != nil && candidate.canBecomeMainWindow)
            return candidate;
    return nil;
}

static bool NTEnsureOverlay(void)
{
    NSWindow* window = NTFindUnityWindow();
    if (window == nil) return gOverlay != nil && gOverlay.window != nil;

    NSView* content = window.contentView;
    if (gOverlay != nil && gOverlay.window == window && gOverlay.superview == content)
    {
        // Keep the overlay on top in case Unity adds its own subviews.
        if (content.subviews.lastObject != gOverlay)
            [content addSubview:gOverlay positioned:NSWindowAbove relativeTo:nil];
        return true;
    }

    if (gOverlay != nil)
    {
        [gOverlay nt_setToolTipAtMousePoint:nil];
        [gOverlay removeFromSuperview];
    }

    gOverlay = [[NTTooltipOverlayView alloc] initWithFrame:content.bounds];
    [content addSubview:gOverlay positioned:NSWindowAbove relativeTo:nil];
    return true;
}


// ---------------------------------------------------------------- custom bubble

// Layout of the float parameter block sent from C# (MacOSTooltipBackend.cs).
enum
{
    NTC_FontSize,       // absolute points, 0 = system size × scale
    NTC_FontScale,
    NTC_Bold,
    NTC_PadX, NTC_PadY, // points, < 0 = default
    NTC_MaxWidth,       // points, 0 = default
    NTC_OverrideColors,
    NTC_BgR, NTC_BgG, NTC_BgB, NTC_BgA,
    NTC_FgR, NTC_FgG, NTC_FgB, NTC_FgA,
    NTC_CornerRadius,   // points, < 0 = default
    NTC_RichText,       // 1 = text/title are encoded rich text runs (see TooltipRichText.Encode)
    NTC_Count
};

static const CGFloat kNTDefaultPadX = 7.0;
static const CGFloat kNTDefaultPadY = 4.0;
static const CGFloat kNTDefaultMaxWidth = 300.0;
static const CGFloat kNTDefaultRadius = 5.0;

// Draws an attributed string at the top-left of its bounds; measured with the same options.
@interface NTBubbleTextView : NSView
@property (nonatomic, copy) NSAttributedString* text;
@end

@implementation NTBubbleTextView
- (BOOL)isFlipped { return YES; }
- (BOOL)isOpaque { return NO; }
- (NSView*)hitTest:(NSPoint)point { return nil; }
- (void)drawRect:(NSRect)dirtyRect
{
    [self.text drawWithRect:self.bounds
                    options:NSStringDrawingUsesLineFragmentOrigin | NSStringDrawingUsesFontLeading];
}
@end

@interface NTBubblePanel : NSPanel
@end

@implementation NTBubblePanel
- (BOOL)canBecomeKeyWindow { return NO; }
- (BOOL)canBecomeMainWindow { return NO; }
@end

static NTBubblePanel* gPanel = nil;
static NSView* gContainer = nil;
static NSVisualEffectView* gMaterial = nil;
static NTBubbleTextView* gText = nil;

static void NTEnsurePanel(void)
{
    if (gPanel != nil) return;

    gPanel = [[NTBubblePanel alloc] initWithContentRect:NSMakeRect(0, 0, 10, 10)
                                              styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel
                                                backing:NSBackingStoreBuffered
                                                  defer:YES];
    gPanel.level = NSPopUpMenuWindowLevel;
    gPanel.opaque = NO;
    gPanel.backgroundColor = NSColor.clearColor;
    gPanel.hasShadow = YES;
    gPanel.ignoresMouseEvents = YES;
    gPanel.releasedWhenClosed = NO;
    gPanel.hidesOnDeactivate = YES;
    gPanel.floatingPanel = YES;
    gPanel.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces |
                                NSWindowCollectionBehaviorTransient |
                                NSWindowCollectionBehaviorFullScreenAuxiliary |
                                NSWindowCollectionBehaviorIgnoresCycle;

    gContainer = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, 10, 10)];
    gContainer.wantsLayer = YES;
    gContainer.layer.masksToBounds = YES;

    gMaterial = [[NSVisualEffectView alloc] initWithFrame:gContainer.bounds];
    gMaterial.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    gMaterial.blendingMode = NSVisualEffectBlendingModeBehindWindow;
    gMaterial.state = NSVisualEffectStateActive;
    if (@available(macOS 10.14, *)) gMaterial.material = NSVisualEffectMaterialToolTip;
    else gMaterial.material = NSVisualEffectMaterialMenu;
    [gContainer addSubview:gMaterial];

    gText = [[NTBubbleTextView alloc] initWithFrame:NSZeroRect];
    [gContainer addSubview:gText];

    gPanel.contentView = gContainer;
}

// Runs separated by U+001E, fields by U+001F: flags, RRGGBBAA, size, scale, delta, text
// flags: 1 bold, 2 italic, 4 underline, 8 strikethrough, 16 colour.
static NSAttributedString* NTRichString(NSString* encoded, NSFont* base, NSColor* fg, BOOL forceBold)
{
    NSFontManager* fm = NSFontManager.sharedFontManager;
    NSMutableAttributedString* out = [NSMutableAttributedString new];
    for (NSString* record in [encoded componentsSeparatedByString:@"\x1E"])
    {
        NSArray<NSString*>* f = [record componentsSeparatedByString:@"\x1F"];
        if (f.count < 6) continue;
        int flags = f[0].intValue;
        unsigned rgba = 0;
        [[NSScanner scannerWithString:f[1]] scanHexInt:&rgba];
        float size = f[2].floatValue, scale = f[3].floatValue, delta = f[4].floatValue;
        NSString* text = f.count > 6 ? [[f subarrayWithRange:NSMakeRange(5, f.count - 5)] componentsJoinedByString:@" "] : f[5];

        CGFloat pointSize = size > 0 ? size : MAX(1.0, base.pointSize * (scale > 0 ? scale : 1.0) + delta);
        NSFont* font = [NSFont fontWithDescriptor:base.fontDescriptor size:pointSize] ?: base;
        if ((flags & 1) || forceBold) font = [fm convertFont:font toHaveTrait:NSBoldFontMask];
        if (flags & 2) font = [fm convertFont:font toHaveTrait:NSItalicFontMask];

        NSMutableDictionary* attrs = [NSMutableDictionary dictionary];
        attrs[NSFontAttributeName] = font;
        attrs[NSForegroundColorAttributeName] = (flags & 16)
            ? [NSColor colorWithSRGBRed:((rgba >> 24) & 255) / 255.0 green:((rgba >> 16) & 255) / 255.0
                                   blue:((rgba >> 8) & 255) / 255.0 alpha:(rgba & 255) / 255.0]
            : fg;
        if (flags & 4) attrs[NSUnderlineStyleAttributeName] = @(NSUnderlineStyleSingle);
        if (flags & 8) attrs[NSStrikethroughStyleAttributeName] = @(NSUnderlineStyleSingle);
        [out appendAttributedString:[[NSAttributedString alloc] initWithString:text attributes:attrs]];
    }
    return out;
}

static NSColor* NTColor(const float* p, int r)
{
    return [NSColor colorWithSRGBRed:p[r] green:p[r + 1] blue:p[r + 2] alpha:p[r + 3]];
}

static NSScreen* NTScreenAt(NSPoint p)
{
    for (NSScreen* s in NSScreen.screens)
        if (NSMouseInRect(p, s.frame, NO)) return s;
    return NSScreen.mainScreen;
}

static CGFloat NTPrimaryHeight(void)
{
    NSScreen* primary = NSScreen.screens.firstObject;
    return primary != nil ? primary.frame.size.height : 0;
}

extern "C"
{
    // Configures the bubble's content and returns its size in points.
    __attribute__((visibility("default"))) void NTMac_CustomPrepare(const char* utf8Text, const char* utf8Title,
                                                                    const float* p, int count,
                                                                    double* outWidth, double* outHeight)
    {
        @autoreleasepool
        {
            if (p == NULL || count < NTC_Count || utf8Text == NULL) return;
            NTEnsurePanel();

            NSFont* base = [NSFont toolTipsFontOfSize:0];
            CGFloat size = p[NTC_FontSize] > 0 ? p[NTC_FontSize] : base.pointSize * MAX(0.1f, p[NTC_FontScale]);
            BOOL bold = p[NTC_Bold] > 0.5f;
            NSFont* font = bold ? [NSFont boldSystemFontOfSize:size] : [NSFont toolTipsFontOfSize:size];
            NSFont* titleFont = [NSFont boldSystemFontOfSize:size];

            BOOL colors = p[NTC_OverrideColors] > 0.5f;
            NSColor* fg = colors ? NTColor(p, NTC_FgR) : NSColor.labelColor;

            NSString* text = [NSString stringWithUTF8String:utf8Text] ?: @"";
            NSString* title = utf8Title != NULL ? [NSString stringWithUTF8String:utf8Title] : nil;

            NSMutableAttributedString* s = [NSMutableAttributedString new];
            BOOL rich = p[NTC_RichText] > 0.5f;
            if (title.length > 0)
            {
                if (rich) [s appendAttributedString:NTRichString(title, font, fg, YES)];
                else [s appendAttributedString:[[NSAttributedString alloc] initWithString:title
                         attributes:@{ NSFontAttributeName: titleFont, NSForegroundColorAttributeName: fg }]];
                [s appendAttributedString:[[NSAttributedString alloc] initWithString:@"\n"
                    attributes:@{ NSFontAttributeName: font, NSForegroundColorAttributeName: fg }]];
            }
            if (rich) [s appendAttributedString:NTRichString(text, font, fg, NO)];
            else [s appendAttributedString:[[NSAttributedString alloc] initWithString:text
                     attributes:@{ NSFontAttributeName: font, NSForegroundColorAttributeName: fg }]];

            CGFloat padX = p[NTC_PadX] >= 0 ? p[NTC_PadX] : kNTDefaultPadX;
            CGFloat padY = p[NTC_PadY] >= 0 ? p[NTC_PadY] : kNTDefaultPadY;
            CGFloat maxWidth = p[NTC_MaxWidth] > 0 ? p[NTC_MaxWidth] : kNTDefaultMaxWidth;
            CGFloat inner = MAX(20.0, maxWidth - 2 * padX);

            NSRect r = [s boundingRectWithSize:NSMakeSize(inner, CGFLOAT_MAX)
                                       options:NSStringDrawingUsesLineFragmentOrigin | NSStringDrawingUsesFontLeading];
            CGFloat textW = ceil(r.size.width), textH = ceil(r.size.height);
            CGFloat w = textW + 2 * padX, h = textH + 2 * padY;

            gText.text = s;
            gText.frame = NSMakeRect(padX, padY, textW, textH); // container is not flipped: y from the bottom
            gText.needsDisplay = YES;

            CGFloat radius = p[NTC_CornerRadius] >= 0 ? p[NTC_CornerRadius] : kNTDefaultRadius;
            gContainer.layer.cornerRadius = radius;
            gMaterial.hidden = colors;
            gContainer.layer.backgroundColor = colors ? NTColor(p, NTC_BgR).CGColor : NSColor.clearColor.CGColor;

            [gPanel setContentSize:NSMakeSize(w, h)];
            gContainer.frame = NSMakeRect(0, 0, w, h);
            [gPanel invalidateShadow];

            if (outWidth != NULL) *outWidth = w;
            if (outHeight != NULL) *outHeight = h;
        }
    }

    // x, y = top-left corner in global coordinates with Y pointing down from the top of the primary screen.
    __attribute__((visibility("default"))) void NTMac_CustomShowAt(double x, double yDown)
    {
        @autoreleasepool
        {
            if (gPanel == nil) return;
            [gPanel setFrameTopLeftPoint:NSMakePoint(x, NTPrimaryHeight() - yDown)];
            [gPanel orderFrontRegardless];
            [gPanel invalidateShadow];
        }
    }

    __attribute__((visibility("default"))) void NTMac_CustomHide(void)
    {
        @autoreleasepool
        {
            if (gPanel != nil) [gPanel orderOut:nil];
        }
    }

    // out[0..3]  Unity view rect      (x, y, w, h)  global points, Y down
    // out[4..5]  mouse position       (x, y)
    // out[6..9]  visible frame of the screen under the mouse (x, y, w, h)
    // out[10]    distance from the cursor hot spot to the bottom of the cursor image
    __attribute__((visibility("default"))) int NTMac_GetGeometry(double* out, int count)
    {
        @autoreleasepool
        {
            if (out == NULL || count < 11) return 0;
            CGFloat H = NTPrimaryHeight();

            NSWindow* window = NTFindUnityWindow();
            NSView* content = window.contentView;
            if (content == nil) return 0;
            NSRect v = [window convertRectToScreen:[content convertRect:content.bounds toView:nil]];
            out[0] = v.origin.x;
            out[1] = H - (v.origin.y + v.size.height);
            out[2] = v.size.width;
            out[3] = v.size.height;

            NSPoint m = NSEvent.mouseLocation;
            out[4] = m.x;
            out[5] = H - m.y;

            NSRect work = NTScreenAt(m).visibleFrame;
            out[6] = work.origin.x;
            out[7] = H - (work.origin.y + work.size.height);
            out[8] = work.size.width;
            out[9] = work.size.height;

            NSCursor* cursor = NSCursor.currentSystemCursor ?: NSCursor.currentCursor;
            CGFloat below = 16.0;
            if (cursor != nil && cursor.image != nil) below = MAX(8.0, cursor.image.size.height - cursor.hotSpot.y);
            out[10] = below;
            return 1;
        }
    }
}

extern "C"
{
    __attribute__((visibility("default"))) int NTMac_Version(void)
    {
        return 3; // 2 = custom bubble API, 3 = rich text
    }

    __attribute__((visibility("default"))) int NTMac_Initialize(void)
    {
        @autoreleasepool
        {
            return NTEnsureOverlay() ? 1 : 0;
        }
    }

    __attribute__((visibility("default"))) void NTMac_SetToolTip(const char* utf8)
    {
        @autoreleasepool
        {
            if (!NTEnsureOverlay()) return;
            NSString* text = utf8 != NULL ? [NSString stringWithUTF8String:utf8] : nil;
            [gOverlay nt_setToolTipAtMousePoint:text];
        }
    }

    __attribute__((visibility("default"))) double NTMac_InitialDelaySeconds(void)
    {
        // System setting: defaults write -g NSInitialToolTipDelay -int <ms>
        NSInteger ms = [[NSUserDefaults standardUserDefaults] integerForKey:@"NSInitialToolTipDelay"];
        return ms > 0 ? ms / 1000.0 : 1.0;
    }

    __attribute__((visibility("default"))) void NTMac_Shutdown(void)
    {
        @autoreleasepool
        {
            if (gPanel != nil)
            {
                [gPanel orderOut:nil];
                gPanel = nil;
                gContainer = nil;
                gMaterial = nil;
                gText = nil;
            }

            if (gOverlay == nil) return;
            [gOverlay nt_setToolTipAtMousePoint:nil];
            [gOverlay removeAllToolTips];
            [gOverlay removeFromSuperview];
            gOverlay = nil;
        }
    }
}
