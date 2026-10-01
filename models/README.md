# models/

ONNX models used by SECURITY.EXE Phase 1. **These binaries are not committed**
to version control (~37 MB of third-party weights); fetch them with either:

```powershell
# Option A - explicit download
powershell -ExecutionPolicy Bypass -File .\scripts\download-models.ps1
```

Option B - just run the app. On first launch it attempts the same download
automatically (only when a file is missing and the machine is online).

## Files

| File | Size | Used for |
| --- | --- | --- |
| `face_detection_yunet_2023mar.onnx` | ~227 KB | Face detection + 5-point landmarks (bounding box, blur/lighting inputs) |
| `face_recognition_sface_2021dec.onnx` | ~36.9 MB | 128-dimensional face embedding (the actual recognition model) |

## Provenance & licence

- **Project:** [opencv/opencv_zoo](https://github.com/opencv/opencv_zoo)
- **Licence:** Apache License 2.0 — commercially usable, no research-only restrictions.
- **Source (raw):**
  - https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx
  - https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx
- **Runtime:** [ONNX Runtime](https://onnxruntime.ai/) (MIT) via `Microsoft.ML.OnnxRuntime`.

Both models are legitimate, publicly published, and documented by OpenCV. They
are never modified, re-hosted, or uploaded anywhere — the application only
loads them from local disk.

## Integrity

The download script rejects any file smaller than the expected minimum size
(100 KB for YuNet, 30 MB for SFace), which catches truncated transfers. If you
need byte-exact verification, compare against the hashes published on the
`opencv_zoo` repository at the tag you trust.
