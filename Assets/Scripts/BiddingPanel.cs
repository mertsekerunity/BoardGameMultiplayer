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

    [HideInInspector] public int slotIndex;
}

public class BiddingPanel : MonoBehaviour
{
    private Func<int, Color> _getPlayerColor;

    [Header("Circles (configure in Inspector)")]
    [SerializeField] private List<BiddingOption> options = new();

    private Action<int> _onBidChosen;     // callback to TurnManager (slotIndex)
    
    // reserved for future UI
    private string _currentPlayerName = "";
    private int _currentPlayerMoney = 0;
    private int _playerCount = 0;

    public void Initialize(Func<int, Color> getPlayerColor)
    {
        _getPlayerColor = getPlayerColor;
    }

    // Call once at the start of the bidding phase.
    public void ResetForNewBidding(int playerCount)
    {
        _playerCount = playerCount;

        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            opt.slotIndex = i;

            opt.taken = false;
            if (opt.chosenByLabel)
            {
                opt.chosenByLabel.text = "";
                opt.chosenByLabel.gameObject.SetActive(false);
            }
                

            // Show/hide gated option; keep others visible
            bool visible = !opt.requiresAtLeast5Players || playerCount >= 5;

            // (Re)bind click
            if (opt.button)
            {
                opt.button.gameObject.SetActive(visible);

                opt.button.onClick.RemoveAllListeners();
                var captured = opt; // capture by value for the lambda
                opt.button.onClick.AddListener(() => OnOptionClicked(captured));
                opt.button.interactable = false; //
            }
        }
        gameObject.SetActive(true);
    }

    public void BeginTurn(string playerName, int playerMoney, Action<int> onBid)
    {
        _currentPlayerName = playerName;
        _currentPlayerMoney = playerMoney;
        _onBidChosen = onBid;

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

        // Disable all buttons so the current player cannot click twice
        foreach (var o in options)
        {
            if (o.button && o.button.gameObject.activeSelf)
            {
                o.button.interactable = false;
            }
        }

        if (_onBidChosen == null) return;

            _onBidChosen?.Invoke(opt.slotIndex);
    }

    public void MarkChoice(int pid, int slotIndex, string playerName)
    {
        if (slotIndex < 0 || slotIndex >= options.Count)
            return;

        var opt = options[slotIndex];

        opt.taken = true;

        if (opt.button)
        {
            opt.button.interactable = false;
        }

        if (opt.chosenByLabel)
        {
            opt.chosenByLabel.gameObject.SetActive(true);
            opt.chosenByLabel.text = playerName;

            if (_getPlayerColor != null)
            {
                opt.chosenByLabel.color = _getPlayerColor(pid);
            }
        }
    }

    public void Close()
    {
        gameObject.SetActive(false);
    }
}
