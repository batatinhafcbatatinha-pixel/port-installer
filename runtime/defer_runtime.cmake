# For ports that configure the shared PS2RetroX project directly.
cmake_language(EVAL CODE "cmake_language(DEFER CALL include [[${CMAKE_CURRENT_LIST_DIR}/extracted_files.cmake]])")
