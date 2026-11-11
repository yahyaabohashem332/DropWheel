# Samples the tests can't make

DropWheel's FFmpeg holds no encoder for WMV, WMA or HEVC (their patents), so these one-second samples were made
once, on 2026-11-11, with another FFmpeg build (wmv2, wmav2, libx265), for the tests that read such files
through Windows. They are test inputs only and never ship.
