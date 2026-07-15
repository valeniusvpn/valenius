// The branded Valenius shield logo, bundled into the app's Resources by build-pkg.sh
// (Logo.png). Used by the About window and the popup header. Falls back to an SF Symbol when
// running unbundled (dev, `.build/debug` — no Resources dir), so both contexts render something.

import SwiftUI

enum BrandAssets {
    private static let logoImage: NSImage? = {
        guard let url = Bundle.main.url(forResource: "Logo", withExtension: "png") else { return nil }
        return NSImage(contentsOf: url)
    }()

    /// SwiftUI logo view at the given point size, or an SF Symbol shield fallback.
    @ViewBuilder
    static func logo(size: CGFloat) -> some View {
        if let image = logoImage {
            Image(nsImage: image)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(width: size, height: size)
        } else {
            Image(systemName: "shield.lefthalf.filled")
                .font(.system(size: size * 0.9))
                .foregroundColor(.white.opacity(0.9))
        }
    }
}
