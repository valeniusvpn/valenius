// QR generation via CoreImage's built-in CIQRCodeGenerator — native, no `qrencode` dependency
// (the concept doc's explicit choice vs. the Linux tray). Used for the MFA enrollment otpauth
// URI.

import AppKit
import CoreImage
import CoreImage.CIFilterBuiltins

enum QRCode {
    static func image(from string: String, scale: CGFloat = 8) -> NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(string.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage else { return nil }
        let scaled = output.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        let rep = NSCIImageRep(ciImage: scaled)
        let image = NSImage(size: rep.size)
        image.addRepresentation(rep)
        return image
    }

    /// Extract the `secret` param from an otpauth:// URI, for the manual-entry fallback.
    static func secret(fromOtpauth uri: String) -> String? {
        guard let comps = URLComponents(string: uri) else { return nil }
        return comps.queryItems?.first(where: { $0.name == "secret" })?.value
    }
}
