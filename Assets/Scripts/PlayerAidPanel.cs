using UnityEngine;

public class PlayerAidPanel : MonoBehaviour
{
    [SerializeField] private GameObject helpPanel;

    private bool _visible;

    public void Toggle()
    {
        if(!UIManager.Instance.CanTogglePlayerAid) return;

        _visible = !_visible;
        helpPanel.SetActive(_visible);
    }

    public void ForceHide()
    {
        _visible=false;
        helpPanel.SetActive(false);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.H))
        {
            Toggle();
        }
    }
}
