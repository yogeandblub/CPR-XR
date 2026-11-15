// One-shot macOS location for the CPR-XR bridge: prints "lat,lon,accuracy_m" or "error: ...".
// The headset is tethered to this Mac, so the Mac's position is the rescuer's position.
// Build: swiftc -O location.swift -o .bin/cprxr-location   (start_bridge.sh does this)
import CoreLocation
import Foundation

final class Locator: NSObject, CLLocationManagerDelegate {
    let manager = CLLocationManager()
    var done = false

    func start() {
        manager.delegate = self
        manager.desiredAccuracy = kCLLocationAccuracyBest
        switch manager.authorizationStatus {
        case .notDetermined: manager.requestWhenInUseAuthorization() // wait for the answer (didChangeAuthorization)
        case .denied, .restricted: finish("error: location permission denied (System Settings > Privacy & Security > Location Services)")
        default: manager.startUpdatingLocation()
        }
    }

    func locationManager(_ m: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        guard let l = locations.last, l.horizontalAccuracy >= 0 else { return }
        finish(String(format: "%.6f,%.6f,%.0f", l.coordinate.latitude, l.coordinate.longitude, l.horizontalAccuracy))
    }

    func locationManager(_ m: CLLocationManager, didFailWithError error: Error) {
        if m.authorizationStatus == .notDetermined { return } // still waiting for the user to answer the prompt
        finish("error: \(error.localizedDescription)")
    }

    func locationManagerDidChangeAuthorization(_ m: CLLocationManager) {
        switch m.authorizationStatus {
        case .denied, .restricted: finish("error: location permission denied (System Settings > Privacy & Security > Location Services)")
        case .notDetermined: break
        default: m.startUpdatingLocation() // allowed: get the fix
        }
    }

    func finish(_ s: String) {
        guard !done else { return }
        done = true
        print(s)
        exit(0)
    }
}

let locator = Locator()
locator.start()
DispatchQueue.main.asyncAfter(deadline: .now() + 60) { locator.finish("error: timed out waiting for permission or a location fix") }
RunLoop.main.run()
