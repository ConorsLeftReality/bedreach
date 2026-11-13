
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Release signing: put these in ~/.gradle/gradle.properties or env vars (never commit the keystore).
//   BEDREACH_KEYSTORE, BEDREACH_KEYSTORE_PASSWORD, BEDREACH_KEY_ALIAS, BEDREACH_KEY_PASSWORD
fun secret(name: String): String? = (project.findProperty(name) as String?) ?: System.getenv(name)

android {
    namespace = "com.bedreach.app"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.bedreach.app"
        minSdk = 26
        targetSdk = 34
        versionCode = 2
        versionName = "1.0.0"
    }

    signingConfigs {
        create("release") {
            val ks = secret("BEDREACH_KEYSTORE")
            if (ks != null && file(ks).exists()) {
                storeFile = file(ks)
                storePassword = secret("BEDREACH_KEYSTORE_PASSWORD")
                keyAlias = secret("BEDREACH_KEY_ALIAS")
                keyPassword = secret("BEDREACH_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
            val rel = signingConfigs.getByName("release")
            // Falls back to the debug key so a fresh clone can still build an installable APK.
            signingConfig = if (rel.storeFile != null) rel else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    testOptions { unitTests.isReturnDefaultValues = true }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
