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
        versionCode = 170
        versionName = "1.7.0"
    }

    signingConfigs {
        create("release") {
            val ksFile = rootProject.file("connectme-release.jks")
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
