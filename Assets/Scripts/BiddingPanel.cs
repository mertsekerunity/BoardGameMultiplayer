using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public class BiddingOption
{
    public Button button;
    public TextMeshProUGUI chosenByLabel; // text that will show the player's name under the circle
    public int amount;                    // e.g. +1, 0, -3, -10 etc.
    public bool requiresAtLeast5Players;  // gate for the special +1 circle when >=5 players

    [HideInInspector] public bool taken = false;
}

public class BiddingPanel : MonoBehaviour
{
    private Func<int, Color> _getPlayerColor;

    [Header("Circles (configure in Inspector)")]
    [SerializeField] private List<BiddingOption> options = new();

    private Action<int> _onBidChosen;     // callback to TurnManager
    private string _currentPlayerName = "";
    private int _currentPlayerMoney = 0;
    private int _playerCount = 0;

    // Call once at the start of the bidding phase.
    public void ResetForNewBidding(int playerCount)
    {
        _playerCount = playerCount;
        foreach (var opt in options)
        {
            opt.taken = false;
            if (opt.chosenByLabel) opt.chosenByLabel.text = "";

            // Show/hide gated option; keep others visible
            bool visible = !opt.requiresAtLeast5Players || playerCount >= 5;
            if (opt.button) opt.button.gameObject.SetActive(visible);

            // (Re)bind click
            if (opt.button)
            {
                opt.button.onClick.RemoveAllListeners();
                var captured = opt; // capture by value for the lambda
                opt.button.onClick.AddListener(() => OnOptionClicked(captured));
                opt.button.interactable = false; //
            }
        }
        gameObject.SetActive(true);
    }

    /// <summary>Called by UIManager/TurnManager each time a player must bid.</summary>
    public void BeginTurn(string playerName, int playerMoney, Action<int> onBid)
    {
        _currentPlayerName = playerName;
        _currentPlayerMoney = playerMoney;
        _onBidChosen = onBid;

        // Enable only not-taken, affordable options (positive amounts must be <= money)
        foreach (var opt in options)
        {
            if (!opt.button || !opt.button.gameObject.activeSelf) continue;
            if (opt.taken)
            {
                opt.button.interactable = false;
                continue;
            }

            bool affordable = (opt.amount <= 0) || (playerMoney >= opt.amount);
            opt.button.interactable = affordable;
        }
    }

    private void OnOptionClicked(BiddingOption opt)
    {
        if (opt.taken) return;

        // Lock this option for the entire bidding phase
        opt.taken = true;

        // Show the current player's name under the circle
        if (opt.chosenByLabel) opt.chosenByLabel.text = _currentPlayerName;

        // Disable all buttons so the current player cannot click twice
        foreach (var o in options)
            if (o.button && o.button.gameObject.activeSelf)
                o.button.interactable = false;

        // Hand the chosen amount back to TurnManager
        _onBidChosen?.Invoke(opt.amount);
    }

    public void Close()
    {
        gameObject.SetActive(false);
    }
}
