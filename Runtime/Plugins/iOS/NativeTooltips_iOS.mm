// NativeTooltips — iOS / iPadOS.
//
// UIKit on iPhone has no desktop-style tooltips (UIToolTipInteraction only works with a pointer on
// iPadOS/Mac Catalyst and can't be triggered programmatically). The native equivalent of an
// "element hint" is a popover (UIPopoverPresentationController) with an arrow pointing at the
// element — we force the popover style on iPhone too (no full-screen adaptation) and pass touches
// through to Unity's view (passthroughViews) so the game keeps working.
//
// Style support: font size / bold, padding, max width, colors (popover background + text),
// placement (arrow direction) and offset (shifts the arrow target), bold title line.
//
// Compiled by Xcode together with the Unity project (source plugin, ARC: -fobjc-arc in the .meta).

#import <UIKit/UIKit.h>

extern "C" UIViewController* UnityGetGLViewController(void);
extern "C" UIView* UnityGetGLView(void);

// Layout of the float parameter block sent from C# (IOSTooltipBackend.cs).
enum
{
    NTP_AnchorX, NTP_AnchorY, NTP_AnchorW, NTP_AnchorH,   // normalized, top-left origin
    NTP_Haptic,
    NTP_FontSize,       // absolute points, 0 = system size × scale
    NTP_FontScale,
    NTP_Bold,
    NTP_PadX, NTP_PadY, // points, < 0 = default
    NTP_MaxWidth,       // points, 0 = default
    NTP_OverrideColors,
    NTP_BgR, NTP_BgG, NTP_BgB, NTP_BgA,
    NTP_FgR, NTP_FgG, NTP_FgB, NTP_FgA,
    NTP_ArrowMask,      // UIPopoverArrowDirection bits
    NTP_OffsetX, NTP_OffsetY, // points, y down
    NTP_RichText,       // 1 = text/title are encoded rich text runs (see TooltipRichText.Encode)
    NTP_Count
};

static const CGFloat kNTDefaultPadH = 12.0;
static const CGFloat kNTDefaultPadV = 8.0;
static const CGFloat kNTDefaultMaxWidth = 300.0;

// ---------------------------------------------------------------- rich text

// Runs separated by U+001E, fields by U+001F: flags, RRGGBBAA, size, scale, delta, text
// flags: 1 bold, 2 italic, 4 underline, 8 strikethrough, 16 colour.
static NSAttributedString* NTRichString(NSString* encoded, UIFont* base, UIColor* fg, BOOL forceBold)
{
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
        UIFontDescriptorSymbolicTraits traits = base.fontDescriptor.symbolicTraits;
        if ((flags & 1) || forceBold) traits |= UIFontDescriptorTraitBold;
        if (flags & 2) traits |= UIFontDescriptorTraitItalic;
        UIFontDescriptor* d = [base.fontDescriptor fontDescriptorWithSymbolicTraits:traits] ?: base.fontDescriptor;
        UIFont* font = [UIFont fontWithDescriptor:d size:pointSize];

        NSMutableDictionary* attrs = [NSMutableDictionary dictionary];
        attrs[NSFontAttributeName] = font;
        attrs[NSForegroundColorAttributeName] = (flags & 16)
            ? [UIColor colorWithRed:((rgba >> 24) & 255) / 255.0 green:((rgba >> 16) & 255) / 255.0
                               blue:((rgba >> 8) & 255) / 255.0 alpha:(rgba & 255) / 255.0]
            : fg;
        if (flags & 4) attrs[NSUnderlineStyleAttributeName] = @(NSUnderlineStyleSingle);
        if (flags & 8) attrs[NSStrikethroughStyleAttributeName] = @(NSUnderlineStyleSingle);
        [out appendAttributedString:[[NSAttributedString alloc] initWithString:text attributes:attrs]];
    }
    return out;
}

@interface NTPopoverDelegate : NSObject <UIPopoverPresentationControllerDelegate>
@end

@implementation NTPopoverDelegate
- (UIModalPresentationStyle)adaptivePresentationStyleForPresentationController:(UIPresentationController*)controller
                                                               traitCollection:(UITraitCollection*)traitCollection
{
    return UIModalPresentationNone; // popover in compact width too (iPhone)
}

- (UIModalPresentationStyle)adaptivePresentationStyleForPresentationController:(UIPresentationController*)controller
{
    return UIModalPresentationNone;
}
@end

@interface NTTooltipViewController : UIViewController
@property (nonatomic, strong) UILabel* label;
@property (nonatomic) CGFloat padH;
@property (nonatomic) CGFloat padV;
@end

@implementation NTTooltipViewController

- (void)viewDidLoad
{
    [super viewDidLoad];

    UILabel* label = [UILabel new];
    label.translatesAutoresizingMaskIntoConstraints = NO;
    label.numberOfLines = 0;
    label.font = [UIFont preferredFontForTextStyle:UIFontTextStyleFootnote];
    label.adjustsFontForContentSizeCategory = YES;
    if (@available(iOS 13.0, *)) label.textColor = UIColor.labelColor;
    else label.textColor = UIColor.blackColor;
    [self.view addSubview:label];
    self.label = label;

    // safeAreaLayoutGuide avoids the popover arrow area.
    UILayoutGuide* g = self.view.safeAreaLayoutGuide;
    [NSLayoutConstraint activateConstraints:@[
        [label.leadingAnchor constraintEqualToAnchor:g.leadingAnchor constant:self.padH],
        [label.trailingAnchor constraintEqualToAnchor:g.trailingAnchor constant:-self.padH],
        [label.topAnchor constraintEqualToAnchor:g.topAnchor constant:self.padV],
        [label.bottomAnchor constraintEqualToAnchor:g.bottomAnchor constant:-self.padV],
    ]];
}

- (void)nt_setText:(NSString*)text title:(NSString*)title params:(const float*)p
{
    [self loadViewIfNeeded];

    // Font: keep Dynamic Type unless the size or weight is customized.
    UIFont* base = [UIFont preferredFontForTextStyle:UIFontTextStyleFootnote];
    BOOL customSize = p[NTP_FontSize] > 0 || fabsf(p[NTP_FontScale] - 1.0f) > 0.001f;
    BOOL bold = p[NTP_Bold] > 0.5f;
    CGFloat size = p[NTP_FontSize] > 0 ? p[NTP_FontSize] : base.pointSize * MAX(0.1f, p[NTP_FontScale]);
    UIFont* font = base;
    if (customSize || bold)
        font = bold ? [UIFont boldSystemFontOfSize:size] : [UIFont systemFontOfSize:size];
    UIFont* titleFont = [UIFont boldSystemFontOfSize:font.pointSize];
    self.label.adjustsFontForContentSizeCategory = !(customSize || bold);

    UIColor* fg = nil;
    if (p[NTP_OverrideColors] > 0.5f)
        fg = [UIColor colorWithRed:p[NTP_FgR] green:p[NTP_FgG] blue:p[NTP_FgB] alpha:p[NTP_FgA]];
    else if (@available(iOS 13.0, *))
        fg = UIColor.labelColor;
    else
        fg = UIColor.blackColor;

    if (p[NTP_RichText] > 0.5f)
    {
        NSMutableAttributedString* s = [NSMutableAttributedString new];
        if (title.length > 0)
        {
            [s appendAttributedString:NTRichString(title, font, fg, YES)];
            [s appendAttributedString:[[NSAttributedString alloc] initWithString:@"\n"
                attributes:@{ NSFontAttributeName: font, NSForegroundColorAttributeName: fg }]];
        }
        [s appendAttributedString:NTRichString(text, font, fg, NO)];
        self.label.attributedText = s;
    }
    else if (title.length > 0)
    {
        NSMutableAttributedString* s = [NSMutableAttributedString new];
        [s appendAttributedString:[[NSAttributedString alloc] initWithString:title
            attributes:@{ NSFontAttributeName: titleFont, NSForegroundColorAttributeName: fg }]];
        [s appendAttributedString:[[NSAttributedString alloc] initWithString:[@"\n" stringByAppendingString:text]
            attributes:@{ NSFontAttributeName: font, NSForegroundColorAttributeName: fg }]];
        self.label.attributedText = s;
    }
    else
    {
        self.label.font = font;
        self.label.textColor = fg;
        self.label.text = text;
    }

    CGFloat maxWidth = p[NTP_MaxWidth] > 0 ? p[NTP_MaxWidth] : kNTDefaultMaxWidth;
    CGSize fit = [self.label sizeThatFits:CGSizeMake(MAX(40.0, maxWidth - 2 * self.padH), CGFLOAT_MAX)];
    self.preferredContentSize = CGSizeMake(ceil(fit.width) + 2 * self.padH, ceil(fit.height) + 2 * self.padV);
}

@end

static NTTooltipViewController* gTooltip = nil;
static NTPopoverDelegate* gDelegate = nil;
static NSUInteger gGeneration = 0;

static void NTPresent(NSString* text, NSString* title, NSData* params, CGRect anchor, NSUInteger generation, int attempt)
{
    if (generation != gGeneration) return; // a Hide/another Show arrived meanwhile

    UIView* unityView = UnityGetGLView();
    UIViewController* root = UnityGetGLViewController();
    if (unityView == nil || root == nil) return;

    const float* p = (const float*)params.bytes;

    // The bubble is already visible — update in place.
    if (gTooltip != nil && gTooltip.presentingViewController != nil && !gTooltip.isBeingDismissed)
    {
        [gTooltip nt_setText:text title:title params:p];
        gTooltip.popoverPresentationController.sourceRect = anchor;
        return;
    }

    UIViewController* presented = root.presentedViewController;
    if (presented != nil)
    {
        // The previous bubble is still disappearing — retry shortly.
        if (presented.isBeingDismissed && attempt < 5)
        {
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.12 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
                NTPresent(text, title, params, anchor, generation, attempt + 1);
            });
        }
        return; // the app is presenting something else (e.g. an alert) — don't interfere
    }

    NTTooltipViewController* vc = [NTTooltipViewController new];
    vc.padH = p[NTP_PadX] >= 0 ? p[NTP_PadX] : kNTDefaultPadH;
    vc.padV = p[NTP_PadY] >= 0 ? p[NTP_PadY] : kNTDefaultPadV;
    vc.modalPresentationStyle = UIModalPresentationPopover;
    [vc nt_setText:text title:title params:p];

    UIPopoverPresentationController* pop = vc.popoverPresentationController;
    pop.sourceView = unityView;
    pop.sourceRect = anchor;
    NSUInteger arrows = (NSUInteger)p[NTP_ArrowMask];
    pop.permittedArrowDirections = arrows != 0 ? arrows : (UIPopoverArrowDirectionDown | UIPopoverArrowDirectionUp);
    pop.passthroughViews = @[ unityView ];
    if (p[NTP_OverrideColors] > 0.5f)
        pop.backgroundColor = [UIColor colorWithRed:p[NTP_BgR] green:p[NTP_BgG] blue:p[NTP_BgB] alpha:p[NTP_BgA]];
    if (gDelegate == nil) gDelegate = [NTPopoverDelegate new];
    pop.delegate = gDelegate;

    gTooltip = vc;
    [root presentViewController:vc animated:YES completion:nil];

    if (p[NTP_Haptic] > 0.5f)
    {
        UIImpactFeedbackGenerator* feedback = [[UIImpactFeedbackGenerator alloc] initWithStyle:UIImpactFeedbackStyleLight];
        [feedback impactOccurred];
    }
}

extern "C"
{
    void NativeTooltips_iOS_Show(const char* utf8Text, const char* utf8Title, const float* params, int count)
    {
        @autoreleasepool
        {
            UIView* unityView = UnityGetGLView();
            if (unityView == nil || utf8Text == NULL || params == NULL || count < NTP_Count) return;

            NSString* text = [NSString stringWithUTF8String:utf8Text];
            NSString* title = utf8Title != NULL ? [NSString stringWithUTF8String:utf8Title] : @"";
            NSData* p = [NSData dataWithBytes:params length:sizeof(float) * NTP_Count]; // copy: C# memory is temporary

            CGRect b = unityView.bounds;
            CGRect anchor = CGRectMake(b.origin.x + params[NTP_AnchorX] * b.size.width + params[NTP_OffsetX],
                                       b.origin.y + params[NTP_AnchorY] * b.size.height + params[NTP_OffsetY],
                                       MAX(1.0, params[NTP_AnchorW] * b.size.width),
                                       MAX(1.0, params[NTP_AnchorH] * b.size.height));

            NTPresent(text, title, p, anchor, ++gGeneration, 0);
        }
    }

    void NativeTooltips_iOS_Hide(void)
    {
        @autoreleasepool
        {
            ++gGeneration;
            if (gTooltip != nil && gTooltip.presentingViewController != nil && !gTooltip.isBeingDismissed)
                [gTooltip dismissViewControllerAnimated:YES completion:nil];
            gTooltip = nil;
        }
    }

    float NativeTooltips_iOS_LongPressDuration(void)
    {
        return (float)[UILongPressGestureRecognizer new].minimumPressDuration; // 0.5 s
    }
}
