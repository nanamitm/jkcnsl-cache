cmake_minimum_required(VERSION 3.21)
project(jkcnsl-qt VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(Qt6 REQUIRED COMPONENTS
    Core
    Quick
    QuickControls2
    Network
    WebSockets
)

if(ANDROID)
    include(FetchContent)
    FetchContent_Declare(
        android_openssl
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
        URL https://github.com/KDAB/android_openssl/archive/refs/heads/master.zip
    )
    FetchContent_MakeAvailable(android_openssl)
    include(${android_openssl_SOURCE_DIR}/android_openssl.cmake)
endif()

qt_standard_project_setup(REQUIRES 6.10)

qt_add_executable(jkcnsl-qt
    src/main.cpp
    src/types.h
    src/channelmodel.h
    src/channelmodel.cpp
    src/channelproxymodel.h
    src/channelproxymodel.cpp
    src/channelswsclient.h
    src/channelswsclient.cpp
    src/commentwsclient.h
    src/commentwsclient.cpp
    src/commentmodel.h
    src/commentmodel.cpp
    src/scheduleservice.h
    src/scheduleservice.cpp
    src/ngfilter.h
    src/ngfilter.cpp
    src/watchwsclient.h
    src/watchwsclient.cpp
    src/loginservice.h
    src/loginservice.cpp
    src/appsettings.h
    src/appsettings.cpp
)

if(WIN32)
    enable_language(RC)
    target_sources(jkcnsl-qt PRIVATE
        resources/windows/app_icon.rc
    )
endif()

qt_add_qml_module(jkcnsl-qt
    URI JkcnslApp
    VERSION 1.0
    QML_FILES
        qml/Main.qml
        qml/ChannelListPage.qml
        qml/ChannelItem.qml
        qml/CommentPage.qml
        qml/CommentOverlay.qml
        qml/SettingsPage.qml
        qml/SchedulePage.qml
        qml/UiIcon.qml
        qml/Colors.js
        qml/LoginPage.qml
)

set_target_properties(jkcnsl-qt PROPERTIES
    WIN32_EXECUTABLE TRUE
    MACOSX_BUNDLE TRUE
)

target_link_libraries(jkcnsl-qt PRIVATE
    Qt6::Core
    Qt6::Quick
    Qt6::QuickControls2
    Qt6::Network
    Qt6::WebSockets
)

if(ANDROID)
    set_target_properties(jkcnsl-qt PROPERTIES
        QT_ANDROID_PACKAGE_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/android"
        QT_ANDROID_PACKAGE_NAME "jp.jkcnsl.android"
        QT_ANDROID_APP_NAME "jkcnsl"
        QT_ANDROID_VERSION_NAME "${PROJECT_VERSION}"
        QT_ANDROID_VERSION_CODE 1
        QT_ANDROID_MIN_SDK_VERSION 28
        QT_ANDROID_TARGET_SDK_VERSION 35
        QT_ANDROID_COMPILE_SDK_VERSION 36
        QT_ANDROID_SDK_BUILD_TOOLS_REVISION 36.0.0
    )
    if(CMAKE_BUILD_TYPE STREQUAL "Release" AND DEFINED ENV{QT_ANDROID_KEYSTORE_PATH})
        set_target_properties(jkcnsl-qt PROPERTIES
            QT_ANDROID_SIGN_APK TRUE
            QT_ANDROID_KEYSTORE_PATH $ENV{QT_ANDROID_KEYSTORE_PATH}
            QT_ANDROID_KEYSTORE_ALIAS $ENV{QT_ANDROID_KEYSTORE_ALIAS}
            QT_ANDROID_KEYSTORE_STORE_PASS $ENV{QT_ANDROID_KEYSTORE_STORE_PASS}
            QT_ANDROID_KEYSTORE_KEY_PASS $ENV{QT_ANDROID_KEYSTORE_KEY_PASS}
        )
    endif()
    add_android_openssl_libraries(jkcnsl-qt)
endif()
