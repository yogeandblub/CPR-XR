using System.Collections;
using UnityEngine;

namespace CPR.Integration
{
    /// <summary>
    /// The headset's own location, if Horizon OS can provide one (Quest 3 has no GPS; Android may estimate
    /// from Wi-Fi). Asks for location permission once. If unavailable, the laptop bridge uses the Mac's
    /// location instead. Log tag: "CPRLoc".
    /// </summary>
    public class HeadsetLocation : MonoBehaviour
    {
        public bool HasFix { get; private set; }
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        public float AccuracyM { get; private set; }

        bool m_Started;

        /// <summary>Start (or refresh) location in the background; call at SOS.</summary>
        public void Begin()
        {
            if (m_Started) return;
            m_Started = true;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.FineLocation))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.FineLocation);
                var w = 0f;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.FineLocation) && w < 15f) { w += Time.unscaledDeltaTime; yield return null; }
            }
#endif
            if (!Input.location.isEnabledByUser)
            {
                Debug.Log("CPRLoc: location services off on the headset - the laptop's location will be used");
                yield break;
            }
            Input.location.Start(50f, 10f);
            var t = 0f;
            while (Input.location.status == LocationServiceStatus.Initializing && t < 15f) { t += Time.unscaledDeltaTime; yield return null; }
            if (Input.location.status != LocationServiceStatus.Running)
            {
                Debug.Log($"CPRLoc: no headset location ({Input.location.status}) - the laptop's location will be used");
                yield break;
            }
            // Keep updating while the app runs.
            while (true)
            {
                var d = Input.location.lastData;
                if (d.timestamp > 0)
                {
                    if (!HasFix) Debug.Log($"CPRLoc: headset location available (accuracy {d.horizontalAccuracy:F0} m)");
                    HasFix = true;
                    Latitude = d.latitude; Longitude = d.longitude; AccuracyM = d.horizontalAccuracy;
                }
                yield return new WaitForSeconds(5f);
            }
        }
    }
}
