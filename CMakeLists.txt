cmake_minimum_required(VERSION 3.10)
project(CameraSDK)

set(CMAKE_CXX_STANDARD 17)

# Include directory
include_directories(src/include)

# Native SDK DLL
add_library(CameraCore SHARED src/CameraCore/CameraCore.cpp)
target_compile_definitions(CameraCore PRIVATE CAMERASDK_EXPORTS)

# Output directory for the DLL
set_target_properties(CameraCore PROPERTIES 
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
