import java.io.FileInputStream
import java.util.Properties

plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

// Release signing. The keystore path + passwords live in android/key.properties (gitignored,
// never committed). When it's absent (CI, a fresh clone, another dev), the release build falls
// back to the debug key so the project still builds.
val keystoreProperties = Properties()
val keystorePropertiesFile = rootProject.file("key.properties")
val hasReleaseKeystore = keystorePropertiesFile.exists()
if (hasReleaseKeystore) {
    keystoreProperties.load(FileInputStream(keystorePropertiesFile))
}

// FCM (push-to-approve) is a Pro feature; the Google-services plugin is only
// applied when the operator has dropped in their google-services.json, so the
// OSS build (which doesn't ship it) still builds without Firebase.
if (file("google-services.json").exists()) {
    apply(plugin = "com.google.gms.google-services")
}

android {
    namespace = "com.stranto.valenius.valenius_mobile"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        // Permanent Play Store identity — do NOT change after the first publish.
        applicationId = "com.stranto.valenius"
        // You can update the following values to match your application needs.
        // For more information, see: https://flutter.dev/to/review-gradle-config.
        minSdk = flutter.minSdkVersion
        targetSdk = flutter.targetSdkVersion
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    signingConfigs {
        if (hasReleaseKeystore) {
            create("release") {
                keyAlias = keystoreProperties["keyAlias"] as String?
                keyPassword = keystoreProperties["keyPassword"] as String?
                storeFile = (keystoreProperties["storeFile"] as String?)?.let { file(it) }
                storePassword = keystoreProperties["storePassword"] as String?
            }
        }
    }

    buildTypes {
        release {
            // Use the real upload key when key.properties is present; otherwise fall back to the
            // debug key so the project still builds without the release secrets.
            signingConfig = if (hasReleaseKeystore) {
                signingConfigs.getByName("release")
            } else {
                signingConfigs.getByName("debug")
            }

            // Disable R8 code shrinking/obfuscation for release. MLKit (used by mobile_scanner
            // for QR scanning) registers its components via reflection (ComponentDiscovery reads
            // *Registrar classes from manifest metadata and calls their no-arg constructors). R8
            // strips those reflectively-reached constructors -> "NoSuchMethodException:
            // BarcodeRegistrar.<init> []" -> the barcode scanner is null -> the camera crashes with
            // "Attempt to invoke virtual method 'f6.c f6.b.a(b6.c)' on a null object reference".
            // Keeping minify off is the simplest robust fix for an enterprise-distributed app.
            // proguard-rules.pro (with MLKit keeps) is still wired below in case minify is ever
            // re-enabled.
            isMinifyEnabled = false
            isShrinkResources = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}
