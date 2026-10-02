plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.korgangames.connectme"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.korgangames.connectme"
        minSdk = 26
        targetSdk = 34
        versionCode = 165
        versionName = "1.6.5"
    }

    signingConfigs {
        create("release") {
            val ksFile = rootProject.file("connectme-release.jks")
            val b64File = rootProject.file("connectme-keystore.b64")
            if (!ksFile.exists() && b64File.exists()) {
                try {
                    val bytes = java.util.Base64.getDecoder().decode(b64File.readText().trim())
                    ksFile.writeBytes(bytes)
                } catch (e: Exception) {
                    println("Failed to decode keystore b64: ${e.message}")
                }
            }
            if (ksFile.exists()) {
                storeFile = ksFile
                storePassword = "connectmepassword123"
                keyAlias = "connectme"
                keyPassword = "connectmepassword123"
            } else {
                initWith(getByName("debug"))
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("release")
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
}
