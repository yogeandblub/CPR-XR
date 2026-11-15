using UnityEngine;
using UnityEngine.SceneManagement;

namespace CPR
{
    /// <summary>
    /// Kill switch for fast test runs: restarts the app (reloads the scene -> back to Idle).
    ///   - hold the menu button (left controller ☰, or the hand menu pinch) for 1.5 s, or
    ///   - say "restart" (SOSController forwards it; voice is off during a live call, the button always works).
    /// Installs itself at startup and survives the reload, so nothing needs adding to the scene.
    /// </summary>
    public class KillSwitch : MonoBehaviour
    {
        public float holdSeconds = 1.5f;
        float m_Held;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            var go = new GameObject("Kill Switch");
            DontDestroyOnLoad(go);
            go.AddComponent<KillSwitch>();
        }

        void Update()
        {
            m_Held = OVRInput.Get(OVRInput.Button.Start) ? m_Held + Time.unscaledDeltaTime : 0f;
            if (m_Held >= holdSeconds) { m_Held = float.NegativeInfinity; Restart("menu button held"); }
        }

        public static void Restart(string why)
        {
            Debug.Log($"CPRKill: restarting the app ({why})");
            // Hang up a live call first so the dispatcher isn't left on an open line.
            foreach (var call in FindObjectsByType<QuestCallBridge>(FindObjectsSortMode.None))
                if (call.IsInCall || call.IsBusy) call.HangUp();
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
    }
}
