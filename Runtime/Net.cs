using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using System.Threading;


/// <summary>
/// TODO 
/// - add boolean message type
/// - add LobbySearch(name)
/// - fix autojoin bug when change password
/// </summary>

namespace WSNet
{
    public class Net
    {
        public static Net instance;
        public string url = "";
        public int port = 80;
        public uint lobbyId = uint.MaxValue;
        public int playerId = -1;
        public string playerName = string.Empty;
        public int adminId = -1;
        public ErrorType lastError;

        public bool connected;

        [Header("Core")]
        public List<Player> players = new List<Player>();
        public List<Lobby> lobbies = new List<Lobby>();
        public Dictionary<int, Player> playersMap = new Dictionary<int, Player>();

        private byte[] buffer;
        readonly MemoryStream messageStream = new MemoryStream(1024);
        readonly BinaryWriter messageWriter;
        readonly BinaryReader messageReader;
        private Websocket ws;
        private const int ToAll = 255;

        private delegate void CommandDelegate (int startIndex, int count);
        readonly Dictionary<Command, CommandDelegate> commands;

        private delegate void MessageTypeDelegate (Msg message, Player sender, int startIndex, int count);
        readonly Dictionary<MessageType, MessageTypeDelegate> messageDecoders;
        readonly CancellationTokenSource ctsSend = new CancellationTokenSource();
        readonly MemoryStream stream = new MemoryStream(1024);
        readonly BinaryWriter sendWriter;

        #region Events
        public UnityEvent onConnectionClose = new UnityEvent();
        public UnityEvent onError = new UnityEvent();
        public class EventLobbyList : UnityEvent<Lobby[]> { }
        public readonly EventLobbyList onLobbyList = new EventLobbyList();
        public class EventLobbyCreate : UnityEvent<bool> { }
        public readonly EventLobbyCreate onLobbyCreate = new EventLobbyCreate();
        public class EventLobbyJoin : UnityEvent<bool> { }
        public readonly EventLobbyJoin onLobbyJoin = new EventLobbyJoin();
        public class EventLobbyGetBans : UnityEvent<bool, BannedPlayer[]> { }
        public readonly EventLobbyGetBans onLobbyGetBanList = new EventLobbyGetBans();
        public class EventLobbyKick : UnityEvent<bool, Player, bool> { }
        public readonly EventLobbyKick onLobbyKickBan = new EventLobbyKick();
        public class EventLobbyLeave : UnityEvent<bool> { }
        public readonly EventLobbyLeave onLobbyLeave = new EventLobbyLeave();
        public class EventLobbyMaxPlayers : UnityEvent<bool, int> { }
        public readonly EventLobbyMaxPlayers onLobbyMaxPlayers = new EventLobbyMaxPlayers();
        public class EventLobbyPassword : UnityEvent<bool> { }
        public readonly EventLobbyPassword onLobbyPasswordChange = new EventLobbyPassword();
        public class EventLobbyPlayerJoined : UnityEvent<Player> { }
        public readonly EventLobbyPlayerJoined onLobbyPlayerJoin = new EventLobbyPlayerJoined();
        public class EventLobbyPlayerLeft : UnityEvent<Player> { }
        public readonly EventLobbyPlayerLeft onLobbyPlayerLeave = new EventLobbyPlayerLeft();
        public class EventLobbyPlayerUsername : UnityEvent<bool, Player> { }
        public readonly EventLobbyPlayerUsername onLobbyPlayerUsername = new EventLobbyPlayerUsername();
        public class EventLobbyTransfer : UnityEvent<bool, Player> { }
        public readonly EventLobbyTransfer onLobbyTransfer = new EventLobbyTransfer();
        public class EventLobbyUnban : UnityEvent<bool, string> { }
        public readonly EventLobbyUnban onLobbyUnban = new EventLobbyUnban();
        public class EventLobbyAllowJoin : UnityEvent<bool, bool> { }
        public readonly EventLobbyAllowJoin onLobbyAllowJoin = new EventLobbyAllowJoin();

        private Websocket.CallbackConnect callbackOnConnection;
        #endregion

        private Net ()
        {
            sendWriter = new BinaryWriter(stream);
            messageReader = new BinaryReader(messageStream);
            messageWriter = new BinaryWriter(messageStream);

            commands = new Dictionary<Command, CommandDelegate>()
            {
                { Command.GameMessage, CommandGameMessage },
                { Command.LobbyAllowJoin, CommandLobbyAllowJoin },
                { Command.LobbyAllowJoinChanged, CommandLobbyAllowJoinChanged },
                { Command.LobbyBans, CommandLobbyBans },
                { Command.LobbyCreate, CommandLobbyCreate },
                { Command.LobbyJoin, CommandLobbyJoin },
                { Command.LobbyKick, CommandLobbyKick },
                { Command.LobbyLeave, CommandLobbyLeave },
                { Command.LobbyList, CommandLobbyList },
                { Command.LobbyMaxPlayers, CommandLobbyMaxPlayers },
                { Command.LobbyMaxPlayersChanged, CommandLobbyMaxPlayersChanged },
                { Command.LobbyPassword, CommandLobbyPassword },
                { Command.LobbyPlayerJoined, CommandLobbyPlayerJoined },
                { Command.LobbyPlayerKicked, CommandLobbyPlayerKicked },
                { Command.LobbyPlayerLeft, CommandLobbyPlayerLeft },
                { Command.LobbyPlayerUsername, CommandLobbyPlayerUsername },
                { Command.LobbyTransfer, CommandLobbyTransfer },
                { Command.LobbyTransferChanged, CommandLobbyTransferChanged },
                { Command.LobbyUnban, CommandLobbyUnban },
                { Command.LobbyUsername, CommandLobbyUsername },
                { Command.Error, CommandError },
            };

            messageDecoders = new Dictionary<MessageType, MessageTypeDelegate>()
            {
                { MessageType.ByteArray, MessageDecoderByteArray },
                { MessageType.Byte, MessageDecoderByte },
                { MessageType.Int, MessageDecoderInt },
                { MessageType.Long, MessageDecoderLong },
                { MessageType.Float, MessageDecoderFloat },
                { MessageType.Double, MessageDecoderDouble },
                { MessageType.String, MessageDecoderString },
                { MessageType.JSON, MessageDecoderJSON },
                { MessageType.Pose, MessageDecoderPose },
                { MessageType.Quaternion, MessageDecoderQuaternion },
                { MessageType.Vector3, MessageDecoderVector3 },
            };
        }

        public static Net GetInstance ()
        {
            if (instance == null)
                instance = new Net();

            return instance;
        }

        #region Internal Events
        private void OnConnectionOpen (ConnectionResult result)
        {
            connected = (result == ConnectionResult.Open);
            callbackOnConnection(result);
        }

        private void OnConnectionClose ()
        {
            onConnectionClose.Invoke();
            connected = false;
        }

        void OnReceive (byte[] buffer, int count)
        {
            this.buffer = buffer;
            //debug message
            var str = "";
            for (var i = 0; i < count; i++)
                str += buffer[i] + " ";

            int index = 0;
            Command cmd = (Command)buffer[index++];

            Debug.Log("NetCommand: " + cmd + '\n' +
                "packet Size: " + count + '\n' +
                "Message: " + str);
            if (commands.TryGetValue(cmd, out CommandDelegate function))
            {
                function(index, count);
            }
            else
            {
                Debug.LogError("WSNet: Unknown command");
            }
        }

        #endregion

        public void Connect (string url, int port, Websocket.CallbackConnect callback)
        {
            callbackOnConnection = callback;
            connected = false;
            ResetLobbyStatus();
            ws = new Websocket
            {
                onConnectionClose = OnConnectionClose,
                onConnectionOpen = OnConnectionOpen,
                onReceive = OnReceive
            };

            this.url = url;
            this.port = port;

            _ = ws.Connect(url, port);
        }

        public void Destroy ()
        {
            if (ws != null)
                _ = ws.CloseAsync();

            instance = null;
        }

        void ResetLobbyStatus ()
        {
            lobbyId = uint.MaxValue;
            playerId = -1;
            playerName = string.Empty;
            adminId = -1;
        }

        public bool IsInLobby ()
        {
            return lobbyId != uint.MaxValue;
        }

        public bool IsAdmin ()
        {
            return adminId != -1 && playerId == adminId;
        }


        #region Game

        #region Receive

        //json
        public delegate void CallbackMessageJSON<T> (Player sender, T value);
        private readonly Dictionary<Msg, Delegate> callbacksJSON = new Dictionary<Msg, Delegate>();
        private readonly Dictionary<Msg, Type> callbacksJSONType = new Dictionary<Msg, Type>();

        public void OnJSON<T> (Msg id, CallbackMessageJSON<T> callback)
        {
            if (callbacksJSON.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksJSON[id] = callback;
            callbacksJSONType[id] = typeof(T);
        }

        //byte array
        /// <summary>
        /// Gives a BinaryReader to read raw data
        /// </summary>
        /// <param name="readr">temp binaryreader to read the data, don't use it oustide this function</param>
        /// <param name="count">number of bytes</param>
        public delegate void CallbackMessageByteArray (Player sender, BinaryReader reader, int count);
        private readonly Dictionary<Msg, CallbackMessageByteArray> callbacksRaw = new Dictionary<Msg, CallbackMessageByteArray>();
        public void On (Msg id, CallbackMessageByteArray callback)
        {
            if (callbacksRaw.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksRaw[id] = callback;
        }

        //byte
        public delegate void CallbackMessageByte (Player sender, byte value);
        private readonly Dictionary<Msg, CallbackMessageByte> callbacksByte = new Dictionary<Msg, CallbackMessageByte>();
        public void On (Msg id, CallbackMessageByte callback)
        {
            if (callbacksByte.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksByte[id] = callback;
        }

        //int
        public delegate void CallbackMessageInt (Player sender, int value);
        private readonly Dictionary<Msg, CallbackMessageInt> callbacksInt = new Dictionary<Msg, CallbackMessageInt>();
        public void On (Msg id, CallbackMessageInt callback)
        {
            if (callbacksInt.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksInt[id] = callback;
        }

        //long
        public delegate void CallbackMessageLong (Player sender, long value);
        private readonly Dictionary<Msg, CallbackMessageLong> callbacksLong = new Dictionary<Msg, CallbackMessageLong>();
        public void On (Msg id, CallbackMessageLong callback)
        {
            if (callbacksLong.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksLong[id] = callback;
        }

        //float
        public delegate void CallbackMessageFloat (Player sender, float value);
        private readonly Dictionary<Msg, CallbackMessageFloat> callbacksFloat = new Dictionary<Msg, CallbackMessageFloat>();
        public void On (Msg id, CallbackMessageFloat callback)
        {
            if (callbacksFloat.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksFloat[id] = callback;
        }

        //double
        public delegate void CallbackMessageDouble (Player sender, double value);
        private readonly Dictionary<Msg, CallbackMessageDouble> callbacksDouble = new Dictionary<Msg, CallbackMessageDouble>();
        public void On (Msg id, CallbackMessageDouble callback)
        {
            if (callbacksDouble.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksDouble[id] = callback;
        }

        //string
        public delegate void CallbackMessageString (Player sender, string value);
        private readonly Dictionary<Msg, CallbackMessageString> callbacksString = new Dictionary<Msg, CallbackMessageString>();
        public void On (Msg id, CallbackMessageString callback)
        {
            if (callbacksString.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksString[id] = callback;
        }


        //vector3
        public delegate void CallbackMessageVector3 (Player sender, Vector3 value);
        private readonly Dictionary<Msg, CallbackMessageVector3> callbacksVector3 = new Dictionary<Msg, CallbackMessageVector3>();
        public void On (Msg id, CallbackMessageVector3 callback)
        {
            if (callbacksVector3.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksVector3[id] = callback;
        }

        //quaternion
        public delegate void CallbackMessageQuaternion (Player sender, Quaternion value);
        private readonly Dictionary<Msg, CallbackMessageQuaternion> callbacksQuaternion = new Dictionary<Msg, CallbackMessageQuaternion>();
        public void On (Msg id, CallbackMessageQuaternion callback)
        {
            if (callbacksQuaternion.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksQuaternion[id] = callback;
        }

        //pose
        public delegate void CallbackMessagePose (Player sender, Pose value);
        private readonly Dictionary<Msg, CallbackMessagePose> callbacksPose = new Dictionary<Msg, CallbackMessagePose>();
        public void On (Msg id, CallbackMessagePose callback)
        {
            if (callbacksPose.ContainsKey(id))
            {
                Debug.LogError("WSNet: a callback with id = (" + id + ") already exists.");
                return;
            }

            callbacksPose[id] = callback;
        }

        #endregion

        #region Send
        private void WriteGameMessageHeader (Msg id, int playerId, MessageType type)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.GameMessage);
            sendWriter.Write((byte)playerId);
            sendWriter.Write((short)id);
            sendWriter.Write((byte)type);
        }

        /// <summary>
        /// send memory stream
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="stream">the stream to send</param>
        public void Send (Msg id, Player player, MemoryStream stream)
        {
            int count = (int)stream.Position;
            stream.Seek(0, SeekOrigin.Begin);
            Send(id, player, stream.GetBuffer(), 0, count);
        }

        /// <summary>
        /// send raw binary buffer
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="buffer">byte buffer</param>
        public void Send (Msg id, Player player, byte[] buffer)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.ByteArray);
            sendWriter.Write(buffer, 0, buffer.Length);

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send raw binary buffer
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="buffer">byte buffer</param>
        /// <param name="index">the start position to read from</param>
        /// <param name="count">the number of bytes to read from the start position</param>
        public void Send (Msg id, Player player, byte[] buffer, int index, int count)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.ByteArray);
            sendWriter.Write(buffer, index, count);

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send byte
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">a byte</param>
        public void SendByte (Msg id, Player player, byte value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Byte);
            sendWriter.Write(value);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send byte
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">a byte</param>
        public void SendByte (Msg id, Player player)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Byte);
            sendWriter.Write((byte)0);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send byte
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">a byte</param>
        public void Send (Msg id, Player player, byte value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Byte);
            sendWriter.Write(value);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send int
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">player or null</param>
        /// <param name="value">integer value</param>
        public void Send (Msg id, Player player, int value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Int);
            sendWriter.Write(value);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);

        }

        /// <summary>
        /// send long
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">integer 64bit value</param>
        public void Send (Msg id, Player player, long value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Long);
            sendWriter.Write(value);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);

        }

        /// <summary>
        /// send float
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">floating point value</param>
        public void Send (Msg id, Player player, float value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Float);
            sendWriter.Write(value);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send double
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">floating point value</param>
        public void Send (Msg id, Player player, double value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Double);
            sendWriter.Write(value);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send string
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">the string to send</param>
        public void Send (Msg id, Player player, string value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.String);
            sendWriter.Write(value);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send vector3
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">the vector3 to send</param>
        public void Send (Msg id, Player player, Vector3 value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Vector3);
            sendWriter.Write(value.x);
            sendWriter.Write(value.y);
            sendWriter.Write(value.z);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send quaternion
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">the quaternion to send</param>
        public void Send (Msg id, Player player, Quaternion value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Quaternion);
            sendWriter.Write(value.x);
            sendWriter.Write(value.y);
            sendWriter.Write(value.z);
            sendWriter.Write(value.w);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send pose
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">the pose to send</param>
        public void Send (Msg id, Player player, Pose value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.Pose);
            sendWriter.Write(value.position.x);
            sendWriter.Write(value.position.y);
            sendWriter.Write(value.position.z);
            sendWriter.Write(value.rotation.x);
            sendWriter.Write(value.rotation.y);
            sendWriter.Write(value.rotation.z);
            sendWriter.Write(value.rotation.w);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// send JSON serialized structure/class
        /// </summary>
        /// <param name="id">message id</param>
        /// <param name="player">the destination player (or null for All players)</param>
        /// <param name="value">the object to send</param>
        public void SendJSON<T> (Msg id, Player player, T value)
        {
            int target = (player != null) ? player.id : ToAll;

            WriteGameMessageHeader(id, target, MessageType.JSON);
            sendWriter.Write(JsonUtility.ToJson(value));
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        #endregion
        #endregion

        #region Lobby
        public void LobbyGetList ()
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyList);
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        public void LobbyCreate (string lobbyName, int maxPlayers, string userName, string password)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyCreate);
            sendWriter.Write(Encoding.UTF8.GetBytes(lobbyName + '\0'));
            sendWriter.Write((byte)maxPlayers);
            sendWriter.Write(Encoding.UTF8.GetBytes(userName + '\0'));
            sendWriter.Write(Encoding.UTF8.GetBytes(password + '\0'));
            playerName = userName;
            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        public void LobbyJoin (uint lobbyId, string username, string password)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyJoin);
            sendWriter.Write(Encoding.UTF8.GetBytes(username + '\0'));
            sendWriter.Write(lobbyId);
            sendWriter.Write(Encoding.UTF8.GetBytes(password + '\0'));

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        public enum LobbySortMode { None, Ascending, Descending }
        public void LobbyJoinAuto (string username, LobbySortMode dateSort = LobbySortMode.None, LobbySortMode playerCountSort = LobbySortMode.None)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyJoinAuto);
            sendWriter.Write((byte)dateSort);
            sendWriter.Write((byte)playerCountSort);
            sendWriter.Write(Encoding.UTF8.GetBytes(username + '\0'));

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        public void LobbyLeave ()
        {
            ResetLobbyStatus();
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyLeave);

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        public void LobbyUsername (string username)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyUsername);
            sendWriter.Write(Encoding.UTF8.GetBytes(username + '\0'));

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        #endregion

        #region Lobby Admin

        /// <summary>
        /// (admin only) lock or unlock the lobby
        /// </summary>
        /// <param name="allow"></param>
        public void LobbyAllowJoin (bool allow)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyAllowJoin);
            sendWriter.Write(allow);

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// (admin only) kick a player
        /// </summary>
        /// <param name="playerId"></param>
        public void LobbyKick (int playerId)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyKick);
            sendWriter.Write((byte)playerId);
            sendWriter.Write((byte)0);//0: kick

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// (admin only) ban a player
        /// </summary>
        /// <param name="playerId"></param>
        public void LobbyBan (int playerId)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyKick);
            sendWriter.Write((byte)playerId);
            sendWriter.Write((byte)1); //1: ban

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// (admin only) unban a previous banned player
        /// </summary>
        /// <param name="shortHash">hash id returned by LobbyGetBanned</param>
        public void LobbyUnban (string shortHash)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyUnban);
            sendWriter.Write(Encoding.UTF8.GetBytes(shortHash + '\0'));

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// (admin only) get the list of banned players
        /// </summary>
        public void LobbyGetBannedPlayers ()
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyBans);

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        ///  (admin only) transfer the lobby ownership to another player
        /// </summary>
        /// <param name="playerId"></param>
        public void LobbyTransfer (int playerId)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyTransfer);
            sendWriter.Write((byte)playerId);

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// (admin only) set the max number of players in the lobby 
        /// </summary>
        /// <param name="maxPlayers"></param>
        public void LobbyMaxPlayers (int maxPlayers)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyTransfer);
            sendWriter.Write((byte)maxPlayers);

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }

        /// <summary>
        /// (admin only) change the lobby password
        /// </summary>
        /// <param name="password"></param>
        public void LobbyPassword (string password)
        {
            sendWriter.Seek(0, SeekOrigin.Begin);
            sendWriter.Write((byte)Command.LobbyPassword);
            sendWriter.Write(Encoding.UTF8.GetBytes(password + '\0'));

            ws.Send(stream.GetBuffer(), 0, (int)stream.Position);
        }
        #endregion

        #region CommandHandlers
        void CommandError (int index, int count)
        {
            lastError = (ErrorType)buffer[index];
            Debug.Log("Net Error: " + lastError);
            onError.Invoke();
        }

        void CommandLobbyList (int index, int count)
        {
            lobbies.Clear();
            while (index < count)
            {
                uint id = ReadUint(ref index);
                string name = ReadString(ref index);
                int players = buffer[index++];
                int maxPlayers = buffer[index++];
                bool hasPassowrd = buffer[index++] != 0;

                lobbies.Add(new Lobby(id, name, players, maxPlayers, hasPassowrd));
            }

            onLobbyList.Invoke(lobbies.ToArray());
        }

        void CommandLobbyCreate (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to create lobby: " + lastError);
                onLobbyCreate.Invoke(true);
                return;
            }

            players.Clear();
            playersMap.Clear();

            lobbyId = ReadUint(ref index);
            playerId = 0;
            adminId = 0;

            string name = ReadString(ref index);
            Player player = new Player(playerId, name);
            players.Add(player);
            playersMap[playerId] = player;

            Debug.Log("Created Lobby. Player: " + name + "(" + playerId + ")");
            onLobbyCreate.Invoke(true);
        }

        void CommandLobbyJoin (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to join lobby: " + lastError);

                onLobbyJoin.Invoke(false);
                return;
            }

            players.Clear();
            playersMap.Clear();
            lobbyId = ReadUint(ref index);
            playerId = buffer[index++];
            adminId = buffer[index++];

            int playerCount = buffer[index++];
            for (int i = 0; i < playerCount; i++)
            {
                int id = buffer[index++];
                string name = ReadString(ref index);
                var player = new Player(id, name);
                players.Add(player);
                playersMap[id] = player;
            }

            playerName = playersMap[playerId].name;

            onLobbyJoin.Invoke(true);
        }

        void CommandLobbyAllowJoin (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to change allowjoin: " + lastError);
                onLobbyAllowJoin.Invoke(false, false);
                return;
            }

            var allowed = buffer[index++] == 1;
            onLobbyAllowJoin.Invoke(true, allowed);
            //todo call onallowjoin(allowed)
        }

        void CommandLobbyAllowJoinChanged (int index, int count)
        {
            var allowed = buffer[index++] == 1;
            onLobbyAllowJoin.Invoke(true, allowed);
        }

        void CommandLobbyBans (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to get banned players: " + lastError);

                onLobbyGetBanList.Invoke(false, null);
                return;
            }

            int bannedCount = ReadUShort(ref index);
            BannedPlayer[] banned = new BannedPlayer[bannedCount];
            for (int i = 0; i < bannedCount; i++)
            {
                string hash = ReadString(ref index);
                string name = ReadString(ref index);
                banned[i] = new BannedPlayer(name, hash);
            }

            onLobbyGetBanList.Invoke(true, banned);
        }

        void CommandLobbyKick (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to kick: " + lastError);

                onLobbyKickBan.Invoke(false, null, false);
                return;
            }

            int playerId = buffer[index++];
            bool ban = buffer[index++] == 1;

            if (playersMap.TryGetValue(playerId, out Player player))
            {
                onLobbyKickBan.Invoke(true, player, ban);
                players.Remove(player);
                playersMap.Remove(playerId);
            }
            else
                onLobbyKickBan.Invoke(false, null, false);
        }

        void CommandLobbyLeave (int index, int count)
        {
            ResetLobbyStatus();
            players.Clear();
            playersMap.Clear();

            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to leave: " + lastError);
                onLobbyLeave.Invoke(true);
            }
            else
                onLobbyLeave.Invoke(false);
        }

        void CommandLobbyMaxPlayers (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to change allowjoin: " + lastError);

                onLobbyMaxPlayers.Invoke(false, -1);
                return;
            }

            var maxPlayers = buffer[index++];
            onLobbyMaxPlayers.Invoke(true, maxPlayers);
        }

        void CommandLobbyMaxPlayersChanged (int index, int count)
        {
            var maxPlayers = buffer[index++];

            onLobbyMaxPlayers.Invoke(true, maxPlayers);
        }

        void CommandLobbyPassword (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to change allowjoin: " + lastError);

                onLobbyPasswordChange.Invoke(false);
                return;
            }

            onLobbyPasswordChange.Invoke(true);
        }

        void CommandLobbyPlayerJoined (int index, int count)
        {
            int playerId = buffer[index++];
            string name = ReadString(ref index);
            var player = new Player(playerId, name);
            players.Add(player);
            playersMap[playerId] = player;
            //todo call onplayerjoin

            onLobbyPlayerJoin.Invoke(player);
        }

        void CommandLobbyPlayerKicked (int index, int count)
        {
            int playerId = buffer[index++];
            bool ban = buffer[index++] == 1;
            //i'm being kicked
            if (playerId == this.playerId)
            {
                if (playersMap.TryGetValue(playerId, out Player player))
                {
                    onLobbyKickBan.Invoke(true, player, ban);
                }
                else
                    onLobbyKickBan.Invoke(false, null, ban);
                ResetLobbyStatus();
                players.Clear();
                playersMap.Clear();

            }
            else if (playersMap.TryGetValue(playerId, out Player player))
            {
                players.Remove(player);
                playersMap.Remove(playerId);
                onLobbyKickBan.Invoke(true, player, ban);
            }
            else
                onLobbyKickBan.Invoke(false, null, ban);
        }

        void CommandLobbyPlayerLeft (int index, int count)
        {
            int playerId = buffer[index++];
            adminId = buffer[index++];

            if (playersMap.TryGetValue(playerId, out Player player))
            {
                players.Remove(player);
                playersMap.Remove(playerId);
                onLobbyPlayerLeave.Invoke(player);
            }
        }

        void CommandLobbyPlayerUsername (int index, int count)
        {
            var playerId = buffer[index++];
            var name = ReadString(ref index);
            if (playersMap.TryGetValue(playerId, out Player player))
            {
                player.name = name;

                onLobbyPlayerUsername.Invoke(true, player);
            }
            else
            {
                Debug.LogWarning("WSNet: unexisting player changed username");
                onLobbyPlayerUsername.Invoke(false, player);
            }
        }

        void CommandLobbyTransfer (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to transfer: " + lastError);

                onLobbyTransfer.Invoke(true, null);
                return;
            }

            adminId = buffer[index++];
            if (playersMap.TryGetValue(adminId, out Player player))
            {
                onLobbyTransfer.Invoke(true, player);
            }
            else
                onLobbyTransfer.Invoke(false, null);
        }

        void CommandLobbyTransferChanged (int index, int count)
        {
            adminId = buffer[index++];

            if (playersMap.TryGetValue(adminId, out Player player))
            {
                onLobbyTransfer.Invoke(true, player);
            }
            else
                onLobbyTransfer.Invoke(false, null);
        }

        void CommandLobbyUnban (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to unban: " + lastError);

                onLobbyUnban.Invoke(false, string.Empty);
                return;
            }
            string shortHash = ReadString(ref index);
            onLobbyUnban.Invoke(true, shortHash);
        }

        void CommandLobbyUsername (int index, int count)
        {
            lastError = (ErrorType)buffer[index++];
            if (lastError != ErrorType.NoError)
            {
                Debug.Log("Impossible to change username: " + lastError);

                onLobbyPlayerUsername.Invoke(false, null);
                //todo call onplayerusername()
                return;
            }

            playerName = ReadString(ref index);
            var player = playersMap[playerId];
            player.name = playerName;
            onLobbyPlayerUsername.Invoke(true, player);
        }

        void CommandGameMessage (int index, int count)
        {
            int senderId = buffer[index++];
            if (!playersMap.TryGetValue(senderId, out Player sender))
                return;

            Msg messageId = (Msg)ReadUShort(ref index);
            MessageType messageType = (MessageType)buffer[index++];


            if (messageDecoders.TryGetValue(messageType, out MessageTypeDelegate function))
            {
                //Debug.Log("GameMsg: " + messageId + ", " + messageType);
                function(messageId, sender, index, count);
            }
            else
            {
                Debug.LogError("Net: Unknown message type");
                lastError = ErrorType.IncorrectType;
                onError.Invoke();
            }
        }
        #endregion

        #region GameMessage Decoders
        void MessageDecoderByteArray (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksRaw.TryGetValue(messageId, out CallbackMessageByteArray function))
            {
                int packetSize = count - index;
                messageWriter.Seek(0, SeekOrigin.Begin);
                messageWriter.Write(buffer, index, packetSize);
                messageReader.BaseStream.Seek(0, SeekOrigin.Begin);
                function(sender, messageReader, packetSize);
            }
        }

        void MessageDecoderByte (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksByte.TryGetValue(messageId, out CallbackMessageByte function))
            {
                byte value = buffer[index++];
                function(sender, value);
            }
        }

        void MessageDecoderInt (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksInt.TryGetValue(messageId, out CallbackMessageInt function))
            {
                int value = ReadInt(ref index);
                function(sender, value);
            }
        }

        void MessageDecoderLong (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksLong.TryGetValue(messageId, out CallbackMessageLong function))
            {
                long value = ReadLong(ref index);
                function(sender, value);
            }
        }

        void MessageDecoderFloat (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksFloat.TryGetValue(messageId, out CallbackMessageFloat function))
            {
                float value = ReadFloat(ref index);
                function(sender, value);
            }
        }

        void MessageDecoderDouble (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksDouble.TryGetValue(messageId, out CallbackMessageDouble function))
            {
                double value = ReadDouble(ref index);
                function(sender, value);
            }
        }

        void MessageDecoderString (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksString.TryGetValue(messageId, out CallbackMessageString function))
            {
                string value = ReadBinaryString(ref index);
                function(sender, value);
            }
        }

        void MessageDecoderJSON (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksJSON.TryGetValue(messageId, out Delegate function))
            {
                string value = ReadBinaryString(ref index);
                Type t = callbacksJSONType[messageId];
                var obj = JsonUtility.FromJson(value, t);
                function.DynamicInvoke(sender, obj);
            }
        }

        void MessageDecoderVector3 (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksVector3.TryGetValue(messageId, out CallbackMessageVector3 function))
            {
                float x = ReadFloat(ref index);
                float y = ReadFloat(ref index);
                float z = ReadFloat(ref index);
                function(sender, new Vector3(x, y, z));
            }
        }

        void MessageDecoderQuaternion (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksQuaternion.TryGetValue(messageId, out CallbackMessageQuaternion function))
            {
                float x = ReadFloat(ref index);
                float y = ReadFloat(ref index);
                float z = ReadFloat(ref index);
                float w = ReadFloat(ref index);
                function(sender, new Quaternion(x, y, z, w));
            }
        }

        void MessageDecoderPose (Msg messageId, Player sender, int index, int count)
        {
            if (callbacksPose.TryGetValue(messageId, out CallbackMessagePose function))
            {
                float x = ReadFloat(ref index);
                float y = ReadFloat(ref index);
                float z = ReadFloat(ref index);
                float qx = ReadFloat(ref index);
                float qy = ReadFloat(ref index);
                float qz = ReadFloat(ref index);
                float qw = ReadFloat(ref index);
                function(sender, new Pose(new Vector3(x, y, z), new Quaternion(qx, qy, qz, qw)));
            }
        }

        #endregion

        #region Utils Functions


        /// <summary>
        /// read float and increase index by 4
        /// </summary>
        /// <param name="index">the index to start from</param>
        /// <returns></returns>
        float ReadFloat (ref int index)
        {
            float val = BitConverter.ToSingle(buffer, index);
            index += 4;
            return val;
        }


        /// <summary>
        /// read float and increase index by 4
        /// </summary>
        /// <param name="index">the index to start from</param>
        /// <returns></returns>
        double ReadDouble (ref int index)
        {
            double val = BitConverter.ToDouble(buffer, index);
            index += 8;
            return val;
        }


        /// <summary>
        /// read int and increase index by 4
        /// </summary>
        /// <param name="index">the index to start from</param>
        /// <returns></returns>
        int ReadInt (ref int index)
        {
            int val = BitConverter.ToInt32(buffer, index);
            index += 4;
            return val;
        }


        /// <summary>
        /// read int and increase index by 4
        /// </summary>
        /// <param name="index">the index to start from</param>
        /// <returns></returns>
        long ReadLong (ref int index)
        {
            long val = BitConverter.ToInt64(buffer, index);
            index += 8;
            return val;
        }

        /// <summary>
        /// read uint and increase index by 4
        /// </summary>
        /// <param name="index">the index to start from</param>
        /// <returns></returns>
        uint ReadUint (ref int index)
        {
            uint val = BitConverter.ToUInt32(buffer, index);
            index += 4;
            return val;
        }

        /// <summary>
        /// read uint and increase index by 2
        /// </summary>
        /// <param name="index">the index to start from</param>
        /// <returns></returns>
        ushort ReadUShort (ref int index)
        {
            ushort val = BitConverter.ToUInt16(buffer, index);
            index += 2;
            return val;
        }


        /// <summary>
        /// read string until null terminator and increases the index
        /// </summary>
        /// <param name="index">the index to start from</param>
        /// <returns></returns>
        string ReadString (ref int index)
        {
            int idx = Array.IndexOf(buffer, (byte)0, index);
            //todo handle error
            if (idx == -1)
            {
                Debug.LogError("Can't find a null terminated string");
                return null;
            }

            int count = idx - index;

            //empty string
            if (count == 0)
            {
                index++;
                return string.Empty;
            }
            string result = Encoding.UTF8.GetString(buffer, index, count);
            index += count + 1;
            return result;
        }

        private string ReadBinaryString (ref int index)
        {
            int stringLength = Read7BitEncodedInt(ref index);
            string result = Encoding.UTF8.GetString(buffer, index, stringLength);
            index += stringLength;
            return result;
        }

        private int Read7BitEncodedInt (ref int index)
        {
            // Read out an Int32 7 bits at a time.  The high bit
            // of the byte when on means to continue reading more bytes.
            int count = 0;
            int shift = 0;
            byte b;
            do
            {
                // Check for a corrupted stream.  Read a max of 5 bytes.
                // In a future version, add a DataFormatException.
                if (shift == 5 * 7)  // 5 bytes max per Int32, shift += 7
                    throw new FormatException("Format_Bad7BitInt32");

                // ReadByte handles end of stream cases for us.
                b = buffer[index++];
                count |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return count;
        }

        #endregion

        enum Command
        {
            Error,
            GameMessage,
            LobbyList,
            LobbyCreate,
            LobbyJoin,
            LobbyJoinAuto,
            LobbyPlayerJoined,
            LobbyLeave,
            LobbyPlayerLeft,
            LobbyTransfer,
            LobbyTransferChanged,
            LobbyAllowJoin,
            LobbyAllowJoinChanged,
            LobbyMaxPlayers,
            LobbyMaxPlayersChanged,
            LobbyKick,
            LobbyPlayerKicked,
            LobbyUsername,
            LobbyPlayerUsername,
            LobbyBans,
            LobbyUnban,
            LobbyPassword,
        }

        public enum ErrorType
        {
            NoError,
            CommandNotFound,
            PlayerNotFound,
            LobbyNotFound,
            Unauthorized,
            WrongPassword,
            MaxLobbyPlayers,
            InputValidationFailed,
            AlreadyInLobby,
            ServerError,
            CallbackNotFound,
            IncorrectType,
        }

        enum MessageType
        {
            Byte,
            ByteArray,
            Int,
            Long,
            Float,
            Double,
            String,
            Vector3,
            Quaternion,
            Pose,
            JSON
        }

        public class Lobby
        {
            public readonly uint id;
            public readonly string name;
            public readonly int players;
            public readonly int maxPlayers;
            public readonly bool hasPassword;

            public Lobby (uint id, string name, int players, int maxPlayers, bool hasPassowrd)
            {
                this.id = id;
                this.name = name;
                this.players = players;
                this.maxPlayers = maxPlayers;
                this.hasPassword = hasPassowrd;
            }

            public bool IsFull ()
            {
                return players == maxPlayers;
            }
        }

        public readonly struct BannedPlayer
        {
            public readonly string hash;
            public readonly string name;

            public BannedPlayer (string name, string id)
            {
                this.hash = id;
                this.name = name;
            }
        }

        public class Player
        {
            readonly public int id;
            public string name;

            public Player (int id, string name)
            {
                this.id = id;
                this.name = name;
            }

            public override string ToString ()
            {
                return "[" + id + "] " + name;
            }
        }

    }


    public static class NetUtils
    {
        public static void WriteVector3 (BinaryWriter br, Vector3 vector)
        {
            br.Write(vector.x);
            br.Write(vector.y);
            br.Write(vector.z);
        }

        public static void WriteQuaternion (BinaryWriter br, Quaternion quaternion)
        {
            br.Write(quaternion.x);
            br.Write(quaternion.y);
            br.Write(quaternion.z);
            br.Write(quaternion.w);
        }

        public static Vector3 ReadVector3 (BinaryReader br)
        {
            return new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }

        public static Quaternion ReadQuaternion (BinaryReader br)
        {
            return new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }
    }
}