# CPR XR

Mixed-reality CPR guidance prototype for Meta Quest 3.

Voice trigger -> scan the scene -> detect a person lying down (YOLO + BlazePose)
-> estimate body size -> place hand-position and kneel markers -> audio + visual CPR guidance.

## Open
Unity **6000.3.10f1** with Android Build Support. Build target: Android (ARM64, Vulkan, OpenXR + Meta Quest).

## Layout
- `Assets/CPR/` - this app's own code, scenes, UI and art
- `Assets/BlazePose/` - Unity's BlazePose sample (Apache-2.0) and models
- `Assets/PassthroughCameraApiSamples/` - Meta's Passthrough Camera API samples (Oculus SDK License), used as reference
- `ThirdPartyNotices/` - licenses for the third-party code above
- `INTEGRATION.md` - how this app connects to the team pipeline repo (`../immerse_the_bay_2026`)

## Safety
Prototype only, not a medical device. Guidance must always prompt the rescuer to call emergency services first.
