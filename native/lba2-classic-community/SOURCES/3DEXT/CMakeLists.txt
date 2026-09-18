# General sources
set(SOURCES_FILES DECORS.CPP GLOBEXT.CPP LBA_EXT.CPP LOADISLE.CPP MAPTOOLS.CPP TERRAIN.CPP)
# set(SOURCES_FILES_ASM BOXZBUF.ASM LINERAIN.ASM)
set(SOURCES_FILES_ASM BOXZBUF.CPP LINERAIN.CPP)

add_library(3dext ${SOURCES_FILES} ${SOURCES_FILES_ASM})
target_compile_definitions(3dext PRIVATE ONE_GAME_DIRECTORY LBA_GAME CDROM)
target_link_libraries(3dext PUBLIC sys svg 3D obj poly ani fio)
target_include_directories(3dext PUBLIC ../../LIB386/H)

# Editor-facing renderer library. Keep this separate from the playable game
# executable so tools can consume the terrain/object renderer without owning
# the game loop or gameplay input.
add_library(lba2_renderer SHARED RENDERER_API.CPP RENDERER_ACTORS.CPP)
set_target_properties(lba2_renderer PROPERTIES OUTPUT_NAME "lba2_renderer")
target_link_libraries(lba2_renderer PUBLIC lba2_renderer_engine)
target_include_directories(lba2_renderer PUBLIC ../../LIB386/H ${CMAKE_CURRENT_SOURCE_DIR} ${CMAKE_SOURCE_DIR}/SOURCES)
target_compile_definitions(lba2_renderer PUBLIC ONE_GAME_DIRECTORY LBA_GAME CDROM)
if(WIN32 AND LBA2_LINK_STATIC AND CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    # Same rationale as SOURCES/CMakeLists.txt's identical block for
    # ${LBA2_EXECUTABLE_NAME}: fold the GCC C++ runtime into the DLL so
    # editor hosts (the C# level editor) don't need MSYS2/MinGW on PATH.
    # svg (linked in transitively via lba2_renderer_engine) already picks
    # up SDL3::SDL3-static instead of the shared import lib once
    # LBA2_LINK_STATIC=ON is set at the top-level configure -- this only
    # needs to add the GCC runtime flags that target is missing.
    target_link_options(lba2_renderer PRIVATE -static-libstdc++ -static-libgcc -static)
endif()


# Configure all ASM files to be recognized as such (workaround for bug where .ASM uppercase extension is not recognized)
# foreach (FILE_ASM IN LISTS SOURCES_FILES_ASM)
#     set_source_files_properties(${FILE_ASM} PROPERTIES LANGUAGE ASM_MASM)
# endforeach ()

# Source files in folder but not used on CMakeScript:
# - DRAWSKY.CPP (but used as included on source file "TERRAIN.CPP")
# - VAR_EXT.CPP (but used as included on source file "GLOBEXT.CPP")
