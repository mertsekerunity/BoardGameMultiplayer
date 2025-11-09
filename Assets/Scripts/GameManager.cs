using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [SerializeField] int playerCount = 4;
    //private int playerCount = 7; // Set via Inspector or dynamically, players should join by clicking in the table!!
    private int maxRound = 7;

    private int currentRound = 1;

    [SerializeField] private TextMeshProUGUI roundText;
    [SerializeField] private TextMeshProUGUI winnerText;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(transform.root.gameObject);
    }

    private void Start()
    {
        InitializeGame(); // runs after all Awake()s
    }
    private void InitializeGame()
    {
        // Set up all core systems and managers
        StockMarketManager.Instance.SetupMarket(playerCount);
        PlayerManager.Instance.SetupPlayers(playerCount);
        DeckManager.Instance.SetupDecks();
        TurnManager.Instance.SetupTurnOrder();

        UIManager.Instance.CreatePlayerPanels();
        UIManager.Instance.CreateMarketRows();

        UIManager.Instance.HideAllUndoButtons();

        // Start the first round
        StartRound();
    }


    public void StartRound()
    {
        roundText.text = currentRound.ToString();
        TurnManager.Instance.RoundStartReset();
        TurnManager.Instance.StartDiscardPhase();

        if (currentRound > 1)
        {
            TurnManager.Instance.BiddingFinished -= OnBiddingFinished;
            TurnManager.Instance.BiddingFinished += OnBiddingFinished;
            TurnManager.Instance.StartBiddingPhase();
            DeckManager.Instance.IncreaseLottery();
            return;
        }

        TurnManager.Instance.SelectionFinished -= OnSelectionFinished;
        TurnManager.Instance.SelectionFinished += OnSelectionFinished;
        TurnManager.Instance.StartCharacterSelectionPhase();
    }

    public void EndRound()
    {
        // Resolve pending manipulations
        TurnManager.Instance.ResolvePendingManipulationsEndOfRound();

        // Resolve close sales: pay and move prices
        StockMarketManager.Instance.ProcessCloseSales();

        // NOW actually remove the sold cards from players' hands
        PlayerManager.Instance.CommitCloseSellsForEndOfRound();

        // Decide when to tax; for now we’ll keep it here:
        TurnManager.Instance.ResolveTaxesEndOfRound();

        // Cleanup round-specific data
        TurnManager.Instance.CleanupRound();
        DeckManager.Instance.CleanupRound();
        UIManager.Instance.ClearAllMarketSecretTags();

        // Advance round counter and begin the next round
        currentRound++;

        if(currentRound <= maxRound)
        {
            StartRound();
        }
        else
        {
            PlayerManager.Instance.SettleRemainingHoldingsToCash();

            var winner = PlayerManager.Instance.players[0];

            List <PlayerManager.Player> otherWinners = new();

            for (int i = 0; i < PlayerManager.Instance.players.Count; i++)
            {
                var player = PlayerManager.Instance.players[i];
                
                if (player.money > winner.money)
                {
                    winner = player;
                    otherWinners.Clear();
                }

                else if (player.money == winner.money)
                {
                    otherWinners.Add(player);
                }
            }

            if(otherWinners != null)
            {
                foreach (var p in otherWinners)
                {
                    if(winner.money == p.money)
                    {
                        if (TurnManager.Instance.GetTotalBidSpend(p.id) > TurnManager.Instance.GetTotalBidSpend(winner.id))
                        {
                            winner = p;
                        }
                    }
                }
            }

            winnerText.text = $"Game is finished. The winner is {winner.playerName}";
            winnerText.gameObject.SetActive(true);
        }
    }

    private void OnBiddingFinished()
    {
        TurnManager.Instance.BiddingFinished -= OnBiddingFinished;
        TurnManager.Instance.SelectionFinished -= OnSelectionFinished;
        TurnManager.Instance.SelectionFinished += OnSelectionFinished;
        TurnManager.Instance.StartCharacterSelectionPhase();
    }

    private void OnSelectionFinished()
    {
        TurnManager.Instance.SelectionFinished -= OnSelectionFinished;
        TurnManager.Instance.StartMainPhase();
    }
}