using UnityEditor;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Brings the Game view to the front whenever Play mode starts.
    ///
    /// The game's intro ("Press ENTER to begin"), HUD and win / lose panels are drawn only in the
    /// Game view. If Play is pressed while the Scene view tab is in front, none of them show and
    /// the game sits in its paused intro state, so it looks as if the menus and the enemies are
    /// broken. This makes sure the Game view is the one you see.
    /// </summary>
    [InitializeOnLoad]
    public static class ShowGameViewOnPlay
    {
        static ShowGameViewOnPlay()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode)
                    EditorApplication.ExecuteMenuItem("Window/General/Game");
            };
        }
    }
}
