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

qt_standard_project_setup(REQUIRES 6.5)

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
    )
endif()
