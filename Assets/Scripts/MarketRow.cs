using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MarketRow : MonoBehaviour
{
    public StockType stockType;

    [SerializeField] private Image iconImage;
    [SerializeField] private TextMeshProUGUI priceText;
    [SerializeField] private Button buyButton;

    [SerializeField] private TextMeshProUGUI privateTagText;

    public void Initialize(StockType type, Sprite icon, Action<StockType> onBuy)
    {
        stockType = type;

        if (iconImage != null)
        {
            iconImage.sprite = icon;
        }
        else
        {
            Debug.Log("icon image is null !!!");
        }

            priceText.text = "0$";

        buyButton.onClick.RemoveAllListeners();
        buyButton.onClick.AddListener(() => onBuy(type));

        ClearPrivateTag();
    }

    public void UpdatePrice(int newPrice)
    {
        priceText.text = $"{newPrice}$";
    }

    public void SetBuyInteractable(bool interactable)
    {
        if (buyButton != null)
        {
            buyButton.interactable = interactable;
        }
    }

    public void SetPrivateTag(string text)
    {
        if (!privateTagText) return;
        privateTagText.gameObject.SetActive(true);
        privateTagText.text = text;
    }

    public void ClearPrivateTag()
    {
        if (!privateTagText) return;
        privateTagText.gameObject.SetActive(false);
        privateTagText.text = "";
    }
}
