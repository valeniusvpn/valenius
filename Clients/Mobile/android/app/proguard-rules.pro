# R8/ProGuard keep rules.
#
# Release builds currently set isMinifyEnabled=false (see build.gradle.kts), so these rules are
# inert today. They exist so that if code shrinking is ever re-enabled, the Google MLKit barcode
# scanner (used by the mobile_scanner plugin for QR pairing) keeps working. MLKit discovers and
# instantiates its components by reflection via no-arg constructors on *Registrar classes; R8
# strips those unless kept, which nulls out the barcode scanner and crashes the camera with
# "NoSuchMethodException: BarcodeRegistrar.<init> []".

# --- Google MLKit (barcode scanning + vision + common) ---
-keep class com.google.mlkit.** { *; }
-keep class com.google.android.gms.internal.mlkit_** { *; }
-keep class com.google.android.gms.vision.** { *; }
-dontwarn com.google.mlkit.**

# Keep classes referenced only by reflection through the ComponentDiscovery registrars.
-keep class * extends com.google.firebase.components.ComponentRegistrar { <init>(); }
-keepnames class com.google.mlkit.**Registrar
-keepclassmembers class com.google.mlkit.**Registrar { <init>(); }
