group = "com.valenius.valenius_vpn"
version = "1.0-SNAPSHOT"

buildscript {
    repositories {
        google()
        mavenCentral()
    }

    dependencies {
        classpath("com.android.tools.build:gradle:9.1.0")
    }
}

allprojects {
    repositories {
        google()
        mavenCentral()
    }
}

plugins {
    id("com.android.library")
}

android {
    namespace = "com.valenius.valenius_vpn"

    compileSdk = 36

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    sourceSets {
        getByName("main") {
            java.srcDirs("src/main/kotlin")
        }
        getByName("test") {
            java.srcDirs("src/test/kotlin")
        }
    }

    defaultConfig {
        minSdk = 24
    }

    testOptions {
        unitTests {
            isIncludeAndroidResources = true
            all {
                it.useJUnitPlatform()

                it.outputs.upToDateWhen { false }

                it.testLogging {
                    events("passed", "skipped", "failed", "standardOut", "standardError")
                    showStandardStreams = true
                }
            }
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

dependencies {
    // Official WireGuard userspace backend (bundles wireguard-go .so).
    // 1.0.20260102+ ships 16 KB-page-aligned native libs (Play requirement for new apps);
    // the 2023 release aligned to 4 KB and failed the 16 KB check. Keep this at a 16 KB build.
    implementation("com.wireguard.android:tunnel:1.0.20260102")

    // Read-only access to the flutter_secure_storage v9 EncryptedSharedPreferences store, so
    // LegacySecureStorage can hand its contents to the v11 store once (see that file).
    // Deprecated upstream but still the only reader for that on-disk format.
    implementation("androidx.security:security-crypto:1.1.0")

    testImplementation("org.jetbrains.kotlin:kotlin-test")
    testImplementation("org.mockito:mockito-core:5.0.0")
}
