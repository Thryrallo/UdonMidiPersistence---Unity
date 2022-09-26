
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class SavedSyncedPickupPosition : UdonSharpBehaviour
{
    public UdonMidiPersistence Persistence;
    public Vector3 Position;
    public Vector3 Rotation;

    [UdonSynced]
    bool _wasMoved = false;

    // ==== Loading Position / Rotation ====

    void Start()
    {
        if(!_wasMoved)
        {
            Position = transform.position;
            Rotation = transform.rotation.eulerAngles;
            Persistence.Request(this.gameObject, nameof(Position), nameof(UpdatePosition));
            Persistence.Request(this.gameObject, nameof(Rotation), nameof(UpdateRotation));
        }
    }

    public void UpdatePosition()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        transform.position = Position;
    }

    public void UpdateRotation()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        transform.rotation = Quaternion.Euler(Rotation);
    }

    // ==== Syncing Position / Rotation, Save on Change ====

    public override void OnPickup()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        _wasMoved = true;
    }

    // ==== Saving Position / Rotation on Drop ====

    public override void OnDrop()
    {
        SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(Save));
        Save();
    }

    public void Save()
    {
        Position = transform.position;
        Rotation = transform.rotation.eulerAngles;
        Persistence.Save(this.gameObject, nameof(Position));
        Persistence.Save(this.gameObject, nameof(Rotation));
    }
}
