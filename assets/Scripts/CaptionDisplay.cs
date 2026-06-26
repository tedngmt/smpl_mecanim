/*
    Standalone on-screen caption overlay. Kept separate from MotionStreamClient so the
    networking/pose logic and the display logic can change independently -- anything
    that wants to show a caption just calls SetCaption()/SetCaptions()/Clear().

    Uses legacy OnGUI (not a Canvas) because the scene has no Canvas/EventSystem set up
    and this project's Unity version predates safely scripting a binary-serialized scene.
*/
using UnityEngine;

public class CaptionDisplay : MonoBehaviour
{
    public bool show = true;
    [Range(10, 40)] public int fontSize = 20;

    string _primary = "";
    string _secondary = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindObjectOfType<CaptionDisplay>() != null)
            return;
        var go = new GameObject("CaptionDisplay (MG-MotionLLM)");
        go.AddComponent<CaptionDisplay>();
    }

    public void SetCaption(string text)
    {
        _primary = text;
        _secondary = "";
    }

    public void SetCaptions(string primary, string secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    public void Clear()
    {
        _primary = "";
        _secondary = "";
    }

    void OnGUI()
    {
        if (!show) return;

        string text = string.IsNullOrEmpty(_secondary) ? _primary : _primary + "\n" + _secondary;
        if (string.IsNullOrEmpty(text)) return;

        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.fontSize = fontSize;
        style.normal.textColor = Color.white;
        style.wordWrap = true;

        Rect box = new Rect(10, Screen.height - 90, Screen.width - 20, 80);
        GUI.Box(box, GUIContent.none);
        GUI.Label(new Rect(box.x + 10, box.y + 5, box.width - 20, box.height - 10), text, style);
    }
}
