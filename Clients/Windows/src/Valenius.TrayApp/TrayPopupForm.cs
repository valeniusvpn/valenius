using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Valenius.Shared;

namespace Valenius.TrayApp;

internal sealed class TrayPopupForm : Form
{
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // ── Row model ─────────────────────────────────────────────────────────────

    private enum RowKind { Header, Profile, Separator, Action }

    private sealed class Row
    {
        public RowKind Kind;
        public string  Text      = "";
        public bool    Connected;
        public bool    Verified;
        public bool    VerificationFailed;
        public string? ProfileName;
        public bool    Enabled   = true;
        public bool    Checked;
        public bool    CanDelete;
        public bool    MfaLocked;    // profile is MFA-gated; show lock glyph, block click
        public bool    ShowLockIcon; // action row: draw lock glyph + amber text
        public Action? OnClick;
        public Rectangle Bounds;
    }

    // ── Base dimensions (at 96 DPI; all scaled by _scale at render time) ───────

    private const int BasePadTop    = 6;
    private const int BasePadBottom = 6;
    private const int BasePadLeft   = 16;
    private const int BaseHeaderH   = 50;
    private const int BaseProfileH  = 40;
    private const int BaseActionH   = 34;
    private const int BaseSepH      = 13;
    private const int BaseDotR      = 5;   // dot radius (diameter = 10)
    private const int BaseIconSz    = 22;
    private const int BaseMinW      = 220;
    private const int BaseBadgeD    = 17;  // verified check-badge diameter

    // ── DPI scaling ───────────────────────────────────────────────────────────

    private float _scale = 1f;
    private int   _padTop, _padBottom, _padLeft, _headerH, _profileH, _actionH, _sepH, _dotR, _iconSz, _minW, _badgeD;
    private int   _w = 240;   // computed per-show from header text measurement

    private int Sc(float v) => (int)MathF.Round(v * _scale);

    // ── Colours ───────────────────────────────────────────────────────────────

    private static readonly Color CBg            = Color.FromArgb(22,  32,  48);
    private static readonly Color CHover         = Color.FromArgb(38,  54,  78);
    private static readonly Color CSep           = Color.FromArgb(48,  66,  92);
    private static readonly Color CHeaderText    = Color.White;
    private static readonly Color CProfileText   = Color.FromArgb(220, 232, 248);
    private static readonly Color CActionText    = Color.FromArgb(195, 212, 235);
    private static readonly Color CDotOn         = Color.FromArgb(0,   210, 110);
    private static readonly Color CDotOff        = Color.FromArgb(90,  108, 130);
    private static readonly Color CConnectedTag  = Color.FromArgb(0,   220, 115);
    private static readonly Color CDimText       = Color.FromArgb(80,  100, 125);
    private static readonly Color CLockAmber     = Color.FromArgb(210, 160,  55);
    private static readonly Color CVerifiedPill  = Color.FromArgb(0,   200, 108);
    private static readonly Color CVerifiedText  = Color.White;
    private const string TagConfirmationFailed = "Confirmation failed";

    // ── Fonts (pixel-sized, rebuilt on scale change) ──────────────────────────

    private Font _fHeader  = null!;
    private Font _fProfile = null!;
    private Font _fAction  = null!;
    private Font _fTag     = null!;

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<string>? ConnectRequested;
    public event Action<string>? DeleteProfileRequested;
    /// <summary>Raised with the profile name to disconnect (per-row toggle).</summary>
    public event Action<string>? DisconnectRequested;
    /// <summary>Raised to disconnect every active tunnel at once (shown only when >1 connected).</summary>
    public event Action?         DisconnectAllRequested;
    public event Action?         UploadConfigRequested;
    public event Action?         AutoConnectToggled;
    public event Action?         InstallUpdateRequested;
    public event Action?         RegisterRequested;
    public event Action?         AboutRequested;
    public event Action?         ExitRequested;
    /// <summary>Raised with the authorize deep link when the user clicks the MFA re-auth row.</summary>
    public event Action<string>? MfaAuthorizeRequested;
    /// <summary>Raised with the otpauth:// URI when the user opens MFA enrollment.</summary>
    public event Action<string>? MfaEnrollRequested;

    // ── Internal state ────────────────────────────────────────────────────────

    private readonly List<Row> _rows    = new();
    private readonly Image     _appIcon;
    private readonly ToolTip   _tooltip = new() { AutoPopDelay = 4000, InitialDelay = 400, ReshowDelay = 200 };
    private int                _hovered    = -1;
    private int                _tooltipRow = -1;
    private Point              _mousePos;

    // ── Construction ──────────────────────────────────────────────────────────

    public TrayPopupForm(Image appIcon)
    {
        _appIcon = appIcon;

        FormBorderStyle = FormBorderStyle.None;
        BackColor       = CBg;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        DoubleBuffered  = true;
        KeyPreview      = true;
        AutoScaleMode   = AutoScaleMode.None;   // fully custom-painted; we scale manually

        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint  |
                 ControlStyles.ResizeRedraw, true);

        ApplyScale(1f);
    }

    // ── DPI scaling ────────────────────────────────────────────────────────────

    private void ApplyScale(float scale)
    {
        if (scale <= 0f) scale = 1f;
        _scale      = scale;
        _padTop     = Sc(BasePadTop);
        _padBottom  = Sc(BasePadBottom);
        _padLeft    = Sc(BasePadLeft);
        _headerH    = Sc(BaseHeaderH);
        _profileH   = Sc(BaseProfileH);
        _actionH    = Sc(BaseActionH);
        _sepH       = Sc(BaseSepH);
        _dotR       = Sc(BaseDotR);
        _iconSz     = Sc(BaseIconSz);
        _minW       = Sc(BaseMinW);
        _badgeD     = Sc(BaseBadgeD);
        RebuildFonts();
    }

    private void RebuildFonts()
    {
        _fHeader?.Dispose();
        _fProfile?.Dispose();
        _fAction?.Dispose();
        _fTag?.Dispose();
        _fHeader  = PxFont(10f,  FontStyle.Bold);
        _fProfile = PxFont(9.5f, FontStyle.Regular);
        _fAction  = PxFont(9f,   FontStyle.Regular);
        _fTag     = PxFont(7.5f, FontStyle.Bold);
    }

    // Point size -> device-independent pixels (pt * 96/72) then scaled by DPI factor.
    private Font PxFont(float pt, FontStyle style) =>
        new("Segoe UI", pt * (96f / 72f) * _scale, style, GraphicsUnit.Pixel);

    private float ScaleForPoint(Point p)
    {
        try
        {
            var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0)
                return dx / 96f;
        }
        catch { /* shcore unavailable — fall back below */ }
        return DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyScale(e.DeviceDpiNew / 96f);
        ComputeWidth();
        ApplyLayout();
        Size = new Size(_w, _padTop + _rows.Sum(r => r.Bounds.Height) + _padBottom);
        Invalidate();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void ShowAt(TunnelStatus status, Point cursor)
    {
        ApplyScale(ScaleForPoint(cursor));
        BuildRows(status);
        ComputeWidth();
        ApplyLayout();

        var screen = Screen.FromPoint(cursor).WorkingArea;
        int h = _padTop + _rows.Sum(r => r.Bounds.Height) + _padBottom;
        int x = Math.Min(cursor.X,          screen.Right  - _w);
        int y = Math.Max(screen.Top, Math.Min(cursor.Y - h, screen.Bottom - h));

        x = Math.Max(screen.Left, x);

        Size     = new Size(_w, h);
        Location = new Point(x, y);
        _hovered    = -1;
        _tooltipRow = -1;
        _tooltip.Hide(this);

        TryRoundCorners();

        if (!Visible) Show();
        Activate();
        Invalidate();
    }

    public void RefreshStatus(TunnelStatus status)
    {
        if (!Visible) return;
        BuildRows(status);
        ApplyLayout();
        Invalidate();
    }

    public void ShowServiceUnavailable(Point cursor)
    {
        ApplyScale(ScaleForPoint(cursor));
        _rows.Clear();
        _rows.Add(new Row { Kind = RowKind.Header,    Text = "Valenius" });
        _rows.Add(new Row { Kind = RowKind.Separator });
        _rows.Add(new Row { Kind = RowKind.Action, Text = "Service unavailable", Enabled = false });
        _rows.Add(new Row { Kind = RowKind.Separator });
        _rows.Add(new Row { Kind = RowKind.Action, Text = "About...", OnClick = () => AboutRequested?.Invoke() });
        _rows.Add(new Row { Kind = RowKind.Action, Text = "Exit",     OnClick = () => ExitRequested?.Invoke() });
        ComputeWidth();
        ApplyLayout();

        var screen = Screen.FromPoint(cursor).WorkingArea;
        int h = _padTop + _rows.Sum(r => r.Bounds.Height) + _padBottom;
        int x = Math.Max(screen.Left, Math.Min(cursor.X, screen.Right  - _w));
        int y = Math.Max(screen.Top,  Math.Min(cursor.Y - h, screen.Bottom - h));

        Size     = new Size(_w, h);
        Location = new Point(x, y);
        _hovered = -1;

        TryRoundCorners();
        if (!Visible) Show();
        Activate();
        Invalidate();
    }

    // ── Row building ──────────────────────────────────────────────────────────

    private void BuildRows(TunnelStatus status)
    {
        _rows.Clear();

        _rows.Add(new Row { Kind = RowKind.Header, Text = "Valenius" });

        if (status.RegistrationIsActive != true)
            _rows.Add(new Row
            {
                Kind    = RowKind.Action,
                Text    = "Register Client",
                OnClick = () => RegisterRequested?.Invoke()
            });

        // ── MFA session gating ────────────────────────────────────────────────
        bool mfaLocked = status.MfaRequired && !string.IsNullOrEmpty(status.MfaAuthorizeUrl);

        if (status.MfaEnrollmentOpen && !string.IsNullOrEmpty(status.MfaEnrollmentUri))
        {
            var uri = status.MfaEnrollmentUri!;
            _rows.Add(new Row
            {
                Kind    = RowKind.Action,
                Text    = "Set up two-factor (MFA)...",
                OnClick = () => MfaEnrollRequested?.Invoke(uri)
            });
        }
        else if (mfaLocked)
        {
            var url = status.MfaAuthorizeUrl!;
            // With a push-approver, prompt to approve on the phone (number-match);
            // tapping still opens the authenticator/TOTP page as the fallback.
            var text = status.MfaApproveNumber is int approveNo
                ? $"Approve {approveNo} on your phone"
                : "Unlock VPN";
            _rows.Add(new Row
            {
                Kind         = RowKind.Action,
                Text         = text,
                ShowLockIcon = true,
                OnClick      = () => MfaAuthorizeRequested?.Invoke(url)
            });
        }

        _rows.Add(new Row { Kind = RowKind.Separator });

        if (status.AvailableProfiles.Length == 0)
        {
            _rows.Add(new Row { Kind = RowKind.Action, Text = "No profiles available", Enabled = false });
        }
        else
        {
            foreach (var p in status.AvailableProfiles)
            {
                var connectedInfo = status.ConnectedTunnels.FirstOrDefault(t =>
                    string.Equals(t.Name, p, StringComparison.OrdinalIgnoreCase));
                var isActive   = connectedInfo is not null;
                var isVerified = connectedInfo?.IsVerified ?? false;
                var verificationFailed = connectedInfo?.VerificationFailed ?? false;
                var canDelete  = status.DeletableProfiles.Contains(p, StringComparer.OrdinalIgnoreCase);
                var pCopy      = p;
                _rows.Add(new Row
                {
                    Kind               = RowKind.Profile,
                    Text               = p,
                    Connected          = isActive && !mfaLocked,
                    Verified           = isVerified && !mfaLocked,
                    VerificationFailed = verificationFailed && !isVerified && !mfaLocked,
                    ProfileName = p,
                    MfaLocked   = mfaLocked,
                    Enabled     = !mfaLocked,
                    CanDelete   = canDelete && !isActive && !mfaLocked,
                    OnClick     = mfaLocked ? null :
                                  (isActive
                                      ? () => DisconnectRequested?.Invoke(pCopy)
                                      : () => ConnectRequested?.Invoke(pCopy))
                });
            }
        }

        // "Disconnect All" only when more than one tunnel is up and not MFA-locked.
        if (!mfaLocked && status.ConnectedTunnels.Length > 1)
        {
            _rows.Add(new Row { Kind = RowKind.Separator });
            _rows.Add(new Row
            {
                Kind    = RowKind.Action,
                Text    = "Disconnect all",
                OnClick = () => DisconnectAllRequested?.Invoke()
            });
        }

        // MFA session active: show expiry inline, grouped with the profile list.
        if (!mfaLocked && status.MfaSessionExpiresAt is { } exp)
        {
            _rows.Add(new Row
            {
                Kind    = RowKind.Action,
                Text    = $"Authorized until {exp.ToLocalTime():HH:mm}",
                Enabled = false
            });
        }

        _rows.Add(new Row { Kind = RowKind.Separator });

        _rows.Add(new Row
        {
            Kind    = RowKind.Action,
            Text    = "Upload Config...",
            OnClick = () => UploadConfigRequested?.Invoke()
        });

        _rows.Add(new Row
        {
            Kind    = RowKind.Action,
            Text    = "Auto Connect",
            Checked = status.AutoConnectEnabled,
            OnClick = () => AutoConnectToggled?.Invoke()
        });

        if (status.UpdateAvailable)
            _rows.Add(new Row
            {
                Kind    = RowKind.Action,
                Text    = "Install update",
                OnClick = () => InstallUpdateRequested?.Invoke()
            });

        _rows.Add(new Row { Kind = RowKind.Separator });

        _rows.Add(new Row { Kind = RowKind.Action, Text = "About...", OnClick = () => AboutRequested?.Invoke() });
        _rows.Add(new Row { Kind = RowKind.Action, Text = "Exit",     OnClick = () => ExitRequested?.Invoke() });
    }

    private int RowH(Row r) => r.Kind switch
    {
        RowKind.Header    => _headerH,
        RowKind.Profile   => _profileH,
        RowKind.Separator => _sepH,
        _                 => _actionH
    };

    private void ComputeWidth()
    {
        int textStart = _padLeft + _iconSz + Sc(8);
        var header = _rows.FirstOrDefault(r => r.Kind == RowKind.Header);
        if (header != null)
        {
            var sz = TextRenderer.MeasureText(header.Text, _fHeader);
            _w = Math.Max(_minW, textStart + sz.Width + _padLeft);
        }
    }

    private void ApplyLayout()
    {
        int y = _padTop;
        foreach (var r in _rows)
        {
            int h = RowH(r);
            r.Bounds = new Rectangle(0, y, _w, h);
            y += h;
        }
    }

    // ── Painting ──────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(CBg);

        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            switch (r.Kind)
            {
                case RowKind.Header:    PaintHeader(g, r);            break;
                case RowKind.Profile:   PaintProfile(g, r, i == _hovered);  break;
                case RowKind.Separator: PaintSeparator(g, r);         break;
                case RowKind.Action:    PaintAction(g, r, i == _hovered);   break;
            }
        }
    }

    private void PaintHeader(Graphics g, Row r)
    {
        int iconY = r.Bounds.Top + (r.Bounds.Height - _iconSz) / 2;
        g.DrawImage(_appIcon, new Rectangle(_padLeft, iconY, _iconSz, _iconSz));

        using var b  = new SolidBrush(CHeaderText);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(r.Text, _fHeader, b,
            new RectangleF(_padLeft + _iconSz + Sc(8), r.Bounds.Top, _w - _padLeft - _iconSz - Sc(20), r.Bounds.Height), sf);
    }

    private void DrawLockGlyph(Graphics g, int cx, int cy, Color color)
    {
        // Shackle: top semi-circle + vertical legs down to body.
        int sw = Sc(6);
        int sr = sw / 2;
        int arcCY    = cy - Sc(4);
        int bodyTopY = cy;
        float pw = MathF.Max(1.4f, _scale * 1.5f);
        using var pen = new Pen(color, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, cx - sr, arcCY - sr, sw, sw, 180, 180);
        g.DrawLine(pen, cx - sr, arcCY, cx - sr, bodyTopY);
        g.DrawLine(pen, cx + sr, arcCY, cx + sr, bodyTopY);

        // Body.
        int bw = Sc(10), bh = Sc(7);
        using var bb = new SolidBrush(color);
        using var bp = RoundRect(new Rectangle(cx - bw / 2, bodyTopY, bw, bh), Sc(2));
        g.FillPath(bb, bp);

        // Keyhole dot.
        int kd = Sc(3);
        using var kh = new SolidBrush(CBg);
        g.FillEllipse(kh, new Rectangle(cx - kd / 2, bodyTopY + Sc(2), kd, kd));
    }

    private void PaintProfile(Graphics g, Row r, bool hot)
    {
        if (hot && r.Enabled && r.OnClick != null)
        {
            using var hb = new SolidBrush(CHover);
            g.FillRectangle(hb, r.Bounds);
        }

        int dotD = _dotR * 2;
        int textX;

        // connection indicator: lock (MFA-gated) | verified badge | dot
        if (r.MfaLocked)
        {
            int lockCx = _padLeft + _dotR;
            int lockCy = r.Bounds.Top + r.Bounds.Height / 2;
            DrawLockGlyph(g, lockCx, lockCy, CLockAmber);
            textX = _padLeft + _dotR * 2 + Sc(10);
        }
        else if (r.Verified)
        {
            int by = r.Bounds.Top + (r.Bounds.Height - _badgeD) / 2;
            var badge = new Rectangle(_padLeft, by, _badgeD, _badgeD);
            using (var bb = new SolidBrush(CDotOn))
                g.FillEllipse(bb, badge);

            using var ckPen = new Pen(CVerifiedText, MathF.Max(1.6f, _scale * 1.7f))
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };
            float bx = badge.Left, byf = badge.Top, d = _badgeD;
            g.DrawLines(ckPen, new PointF[]
            {
                new(bx + d * 0.28f, byf + d * 0.52f),
                new(bx + d * 0.43f, byf + d * 0.68f),
                new(bx + d * 0.74f, byf + d * 0.33f)
            });
            textX = _padLeft + _badgeD + Sc(10);
        }
        else
        {
            int dotY = r.Bounds.Top + (r.Bounds.Height - dotD) / 2;
            var dotColor = r.VerificationFailed ? CLockAmber : (r.Connected ? CDotOn : CDotOff);
            using var db = new SolidBrush(dotColor);
            g.FillEllipse(db, new Rectangle(_padLeft, dotY, dotD, dotD));
            textX = _padLeft + dotD + Sc(10);
        }

        // reserve space on the right for the verified pill / connected / confirmation-failed tag
        int tagReserve = r.Verified ? Sc(78)
                       : r.VerificationFailed ? TextRenderer.MeasureText(TagConfirmationFailed, _fTag).Width + Sc(16)
                       : (r.Connected ? Sc(72) : 0);

        // name
        var nameColor = r.Enabled ? CProfileText : CDimText;
        using (var nb = new SolidBrush(nameColor))
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
            g.DrawString(r.Text, _fProfile, nb,
                new RectangleF(textX, r.Bounds.Top, _w - textX - tagReserve - Sc(8), r.Bounds.Height), sf);

        if (r.Verified)
        {
            // green "Verified" pill, right-aligned
            const string label = "Verified";
            var ts    = TextRenderer.MeasureText(label, _fTag);
            int pillW = ts.Width + Sc(14);
            int pillH = Sc(18);
            int pillX = _w - _padLeft - pillW;
            int pillY = r.Bounds.Top + (r.Bounds.Height - pillH) / 2;
            var pill  = new Rectangle(pillX, pillY, pillW, pillH);

            using (var pb = new SolidBrush(CVerifiedPill))
            using (var path = RoundRect(pill, pillH / 2))
                g.FillPath(pb, path);

            using var tb  = new SolidBrush(CVerifiedText);
            using var tsf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, _fTag, tb, pill, tsf);
        }
        else if (r.VerificationFailed)
        {
            // amber "Confirmation failed" tag — not an error state, verification keeps
            // retrying in the background and this clears on a later success.
            using var tb  = new SolidBrush(CLockAmber);
            using var tsf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far };
            g.DrawString(TagConfirmationFailed, _fTag, tb,
                new RectangleF(textX, r.Bounds.Top, _w - textX - _padLeft, r.Bounds.Height), tsf);
        }
        else if (r.Connected)
        {
            // plain "Connected" tag (green text)
            using var tb  = new SolidBrush(CConnectedTag);
            using var tsf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far };
            g.DrawString("Connected", _fTag, tb,
                new RectangleF(textX, r.Bounds.Top, _w - textX - _padLeft, r.Bounds.Height), tsf);
        }

        // Delete × (only for user-uploaded profiles, visible on hover)
        if (r.CanDelete && hot)
        {
            var dz     = DeleteZone(r);
            bool hotX  = dz.Contains(_mousePos);
            var xColor = hotX ? Color.FromArgb(220, 80, 80) : Color.FromArgb(90, 110, 135);
            using var xb  = new SolidBrush(xColor);
            using var xsf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("×", _fHeader, xb, dz, xsf);
        }
    }

    private Rectangle DeleteZone(Row r) =>
        new(r.Bounds.Right - Sc(26), r.Bounds.Top + Sc(8), Sc(18), r.Bounds.Height - Sc(16));

    private void PaintSeparator(Graphics g, Row r)
    {
        int midY = r.Bounds.Top + r.Bounds.Height / 2;
        using var p = new Pen(CSep);
        g.DrawLine(p, _padLeft, midY, _w - _padLeft, midY);
    }

    private void PaintAction(Graphics g, Row r, bool hot)
    {
        if (hot && r.Enabled)
        {
            using var hb = new SolidBrush(CHover);
            g.FillRectangle(hb, r.Bounds);
        }

        if (r.ShowLockIcon)
        {
            int iconCx = _padLeft + _dotR;
            int iconCy = r.Bounds.Top + r.Bounds.Height / 2;
            DrawLockGlyph(g, iconCx, iconCy, CLockAmber);
            int textX = _padLeft + _dotR * 2 + Sc(10);
            using var ab  = new SolidBrush(CLockAmber);
            using var asf = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(r.Text, _fAction, ab,
                new RectangleF(textX, r.Bounds.Top, _w - textX - _padLeft, r.Bounds.Height), asf);
        }
        else
        {
            var color = r.Enabled ? CActionText : CDimText;
            using var b  = new SolidBrush(color);
            using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(r.Text, _fAction, b,
                new RectangleF(_padLeft, r.Bounds.Top, _w - _padLeft * 2, r.Bounds.Height), sf);
        }

        if (r.Checked)
        {
            int dotD = _dotR * 2;
            int dotY = r.Bounds.Top + (r.Bounds.Height - dotD) / 2;
            using var db = new SolidBrush(CDotOn);
            g.FillEllipse(db, new Rectangle(_w - _padLeft - dotD - Sc(2), dotY, dotD, dotD));
        }
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.Left,          r.Top,            d, d, 180, 90);
        path.AddArc(r.Right - d,     r.Top,            d, d, 270, 90);
        path.AddArc(r.Right - d,     r.Bottom - d,     d, d,   0, 90);
        path.AddArc(r.Left,          r.Bottom - d,     d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    // ── Mouse ─────────────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mousePos = e.Location;
        int h = HitTest(e.Location);
        if (h != _hovered) { _hovered = h; Invalidate(); }
        else Invalidate(); // repaint so × colour updates as mouse moves within the row

        // Tooltip: connected rows → "Click to disconnect"; MFA-locked rows → auth hint.
        var wantRow = h >= 0
            && _rows[h].Kind == RowKind.Profile
            && (_rows[h].Connected || _rows[h].MfaLocked)
            && !DeleteZone(_rows[h]).Contains(e.Location)
            ? h : -1;

        if (wantRow != _tooltipRow)
        {
            _tooltipRow = wantRow;
            if (wantRow >= 0)
            {
                var tip = _rows[wantRow].MfaLocked
                    ? "MFA required — click Unlock VPN to authenticate"
                    : "Click to disconnect";
                _tooltip.Show(tip, this, e.X + Sc(14), e.Y + Sc(14));
            }
            else
                _tooltip.Hide(this);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered    = -1;
        _tooltipRow = -1;
        _tooltip.Hide(this);
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int idx = HitTest(e.Location);
            if (idx >= 0)
            {
                var row = _rows[idx];
                if (row.CanDelete && DeleteZone(row).Contains(e.Location))
                {
                    Hide();
                    if (row.ProfileName is not null)
                        DeleteProfileRequested?.Invoke(row.ProfileName);
                    return;
                }
                if (row.Enabled && row.OnClick != null)
                {
                    Hide();
                    row.OnClick();
                }
            }
        }
        base.OnMouseClick(e);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r.Kind != RowKind.Header && r.Kind != RowKind.Separator && r.Bounds.Contains(p))
                return i;
        }
        return -1;
    }

    // ── Keyboard / focus ──────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Hide();
        base.OnKeyDown(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        Hide();
        base.OnDeactivate(e);
    }

    // ── Win32 helpers ─────────────────────────────────────────────────────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    private void TryRoundCorners()
    {
        try
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
        }
        catch { /* Windows 10 — no rounded corners, that's fine */ }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fHeader?.Dispose();
            _fProfile?.Dispose();
            _fAction?.Dispose();
            _fTag?.Dispose();
            _tooltip.Dispose();
        }
        base.Dispose(disposing);
    }
}
