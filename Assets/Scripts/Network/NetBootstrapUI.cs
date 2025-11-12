using Mirror;
using UnityEngine;

public class NetBootstrapUI : MonoBehaviour
{
    // call from UI buttons
    public void StartHost() => NetworkManager.singleton.StartHost();
    public void StartClient() => NetworkManager.singleton.StartClient();
    public void StopHostOrClient()
    {
        if (NetworkServer.active && NetworkClient.isConnected)
        {
            NetworkManager.singleton.StopHost();
        }
        else if (NetworkClient.isConnected)
        {
            NetworkManager.singleton.StopClient();
        }
    }
}
