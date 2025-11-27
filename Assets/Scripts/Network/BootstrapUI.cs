using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class BootstrapUI : MonoBehaviour
{
    [Header("Details Panel")]
    [SerializeField] private GameObject detailsPanel;
    [SerializeField] private TMP_InputField nameInput;
    [SerializeField] private TMP_InputField addressInput;

    [Header("Player Count Toggles (Host only)")]
    [SerializeField] private GameObject playerCountPanel;
    [SerializeField] private ToggleGroup playerCountGroup;
    [SerializeField] private Toggle toggle4;
    [SerializeField] private Toggle toggle5;
    [SerializeField] private Toggle toggle6;

    [Header("Buttons (optional)")]
    [SerializeField] private Button hostButton;
    [SerializeField] private TextMeshProUGUI hostButtonLabel;
    [SerializeField] private Button clientButton;
    [SerializeField] private TextMeshProUGUI clientButtonLabel;
    [SerializeField] private Button cancelButton;

    private enum Mode
    {
        None,
        HostPending,
        ClientPending
    }

    private Mode currentMode = Mode.None;

    private void Awake()
    {
        if(nameInput != null)
        {
            nameInput.text = string.Empty;
        }

        if (addressInput != null)
        {
            addressInput.text = string.Empty;
        }
    }

    private void Start()
    {
        EnterNeutralState();
    }

    private void EnterNeutralState()
    {
        currentMode = Mode.None;

        if (hostButton != null)
        {
            hostButton.gameObject.SetActive(true);
            hostButton.interactable = true;
        }
        if (clientButton != null)
        {
            clientButton.gameObject.SetActive(true);
            clientButton.interactable = true;
        }

        if (hostButtonLabel != null) hostButtonLabel.text = "Start Host";
        if (clientButton != null) clientButtonLabel.text = "Start Client";

        if (cancelButton != null)
        {
            cancelButton.gameObject.SetActive(false);
        }

        if (detailsPanel != null) detailsPanel.SetActive(false);
        if (playerCountPanel != null) playerCountPanel.SetActive(false);

        // Default player count: 4
        if (toggle4 != null) toggle4.isOn = true;
    }

    public void OnClickHost()
    {
        var nm = CustomNetworkManager.Instance;
        if (nm == null) return;

        if (currentMode != Mode.HostPending)
        {
            currentMode = Mode.HostPending;

            if (clientButton != null) clientButton.gameObject.SetActive(false);

            if (cancelButton != null) cancelButton.gameObject.SetActive(true);

            if (detailsPanel != null) detailsPanel.SetActive(true);

            if (addressInput != null) addressInput.gameObject.SetActive(false);

            if (playerCountPanel != null) playerCountPanel.SetActive(true);

            if (hostButtonLabel != null) hostButtonLabel.text = "Create Game";

            return;
        }

        string playerName = string.IsNullOrWhiteSpace(nameInput?.text)
            ? "Player"
            : nameInput.text.Trim();

        nm.pendingPlayerName = playerName;

        int selectedCount = GetSelectedPlayerCount();
        nm.SetRequiredPlayers(selectedCount);

        nm.StartHost();
    }

    public void OnClickJoin()
    {
        var nm = CustomNetworkManager.Instance;
        if (nm == null) return;

        if (currentMode != Mode.ClientPending)
        {
            currentMode = Mode.ClientPending;

            if (hostButton != null) hostButton.gameObject.SetActive(false);

            if (cancelButton != null) cancelButton.gameObject.SetActive(true);

            if (detailsPanel != null) detailsPanel.SetActive(true);

            if (addressInput != null) addressInput.gameObject.SetActive(true);

            if (playerCountPanel != null) playerCountPanel.SetActive(false);

            if (clientButtonLabel != null) clientButtonLabel.text = "Join Game";

            // Lokal test için istersen default "localhost" koyabilirsin:
            // if (string.IsNullOrWhiteSpace(addressInput.text)) addressInput.text = "localhost";

            return;
        }

        string playerName = string.IsNullOrWhiteSpace(nameInput.text)
            ? "Player"
            : nameInput.text.Trim();

        nm.pendingPlayerName = playerName; // TODO: use with CmdSetPlayerName 

        if (!string.IsNullOrWhiteSpace(addressInput.text))
        {
            nm.networkAddress = addressInput.text.Trim();
        }

        nm.StartClient();
    }

    public void OnClickCancel()
    {
        EnterNeutralState();
    }

    private int GetSelectedPlayerCount()
    {
        if (toggle6 != null && toggle6.isOn) return 6;
        if (toggle5 != null && toggle5.isOn) return 5;
        if (toggle4 != null && toggle4.isOn) return 4;

        return 4;
    }
}
