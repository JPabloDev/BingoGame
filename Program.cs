using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class Program
{
    // Configuración
    const int PORT = 5000;
    const string HOST = "127.0.0.1";
    static readonly Random rng = new Random();

    // Mensajes simples por línea (terminados en \n)
    // Tipos: CARD|... , BALL|B12 , BINGO_CLAIM|playerId , BINGO_VALID|playerId , BINGO_INVALID|playerId , GAME_OVER|playerId

    static void Main()
    {
        Console.WriteLine("¿Quieres ejecutar como (S)ervidor o (C)liente?");
        string modo = Console.ReadLine().ToUpper();

        if (modo == "S")
        {
            RunServer();
        }
        else if (modo == "C")
        {
            RunClient("Usuario");

        }
    }

    #region SERVER
    class ClientInfo
    {
        public TcpClient Tcp;
        public NetworkStream Stream;
        public string Id;
        public int[,] Card; // 5x5 numbers, 0 for FREE
    }

    static void RunServer()
    {
        Console.Title = "Bingo Server";
        TcpListener listener = new TcpListener(IPAddress.Parse(HOST), PORT);
        listener.Start();
        Console.WriteLine($"Servidor bingo escuchando en {HOST}:{PORT}");

        var clients = new List<ClientInfo>();
        var clientsLock = new object();

        // Ball drawing state
        var drawnBalls = new HashSet<string>();
        var allBalls = GenerateAllBalls();
        var ballLock = new object();
        bool gameEnded = false;
        string winnerId = null;

        // Thread: accept clients
        Thread acceptThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var tcp = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        var stream = tcp.GetStream();
                        // First message client should send: HELLO|playerName
                        string hello = ReadLineFromStream(stream);
                        string playerName = $"Player{rng.Next(1000, 9999)}";
                        if (!string.IsNullOrEmpty(hello) && hello.StartsWith("HELLO|"))
                            playerName = hello.Substring("HELLO|".Length).Trim();

                        var client = new ClientInfo
                        {
                            Tcp = tcp,
                            Stream = stream,
                            Id = playerName,
                            Card = GenerateCard()
                        };

                        lock (clientsLock)
                        {
                            clients.Add(client);
                        }

                        Console.WriteLine($"Conexión: {playerName} - clientes totales: {clients.Count}");
                        // Send card to client
                        SendLine(stream, "CARD|" + SerializeCard(client.Card));

                        // Start read loop for that client
                        try
                        {
                            while (tcp.Connected)
                            {
                                string line = ReadLineFromStream(stream);
                                if (line == null) break;
                                if (line.StartsWith("BINGO_CLAIM|"))
                                {
                                    string claimant = line.Substring("BINGO_CLAIM|".Length);
                                    Console.WriteLine($"Reclamo BINGO de {claimant}");
                                    lock (ballLock)
                                    {
                                        if (!gameEnded)
                                        {
                                            // validate claimant's card
                                            var c = client; // for simplicity we validate the card we stored for this socket
                                            if (ValidateBingo(c.Card, drawnBalls))
                                            {
                                                // winner!
                                                gameEnded = true;
                                                winnerId = claimant;
                                                Console.WriteLine($"BINGO VALIDO: {winnerId}");
                                                Broadcast(clients, $"BINGO_VALID|{winnerId}");
                                                Broadcast(clients, $"GAME_OVER|{winnerId}");
                                                // stop drawing thread by setting gameEnded true
                                            }
                                            else
                                            {
                                                Console.WriteLine($"BINGO INVALIDO por {claimant}");
                                                SendLine(client.Stream, $"BINGO_INVALID|{claimant}");
                                            }
                                        }
                                    }
                                }
                                // ignore others
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Cliente desconectado/error: " + ex.Message);
                        }
                        finally
                        {
                            lock (clientsLock)
                            {
                                clients.Remove(client);
                            }
                            try { tcp.Close(); } catch { }
                            Console.WriteLine($"Cliente {playerName} desconectado. Quedan {clients.Count}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error aceptando cliente: " + ex.Message);
                }
            }
        })
        { IsBackground = true };
        acceptThread.Start();

        // Thread: draw balls periodically until someone wins or no balls left
        Thread drawThread = new Thread(() =>
        {
            var remainingBalls = new List<string>(allBalls);
            while (true)
            {
                lock (ballLock)
                {
                    if (gameEnded) break;
                    if (remainingBalls.Count == 0) { Console.WriteLine("No quedan balotas."); break; }
                    int idx = rng.Next(remainingBalls.Count);
                    string ball = remainingBalls[idx];
                    remainingBalls.RemoveAt(idx);
                    drawnBalls.Add(ball);
                    Console.WriteLine($"Balota: {ball}  (total sacadas: {drawnBalls.Count})");
                    // Broadcast to clients
                    lock (clientsLock)
                    {
                        Broadcast(clients, $"BALL|{ball}");
                    }
                }
                // wait a bit between draws, shorter if few players or faster testing
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(250); // small sleeps to be responsive to game end
                    lock (ballLock)
                    {
                        if (gameEnded) break;
                    }
                }
                lock (ballLock)
                {
                    if (gameEnded) break;
                }
            }
            Console.WriteLine("Hilo de sorteo finalizado.");
        })
        { IsBackground = true };
        drawThread.Start();

        // Server console commands
        Console.WriteLine("Comandos: 'status' (muestra jugadores), 'balls' (muestra balotas sacadas), 'exit'");
        while (true)
        {
            var cmd = Console.ReadLine();
            if (cmd == null) break;
            if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
            if (cmd.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                lock (clientsLock)
                {
                    Console.WriteLine($"Clientes conectados: {clients.Count}");
                    foreach (var c in clients) Console.WriteLine($" - {c.Id}");
                }
            }
            else if (cmd.Equals("balls", StringComparison.OrdinalIgnoreCase))
            {
                lock (ballLock)
                {
                    Console.WriteLine($"Balotas sacadas: {drawnBalls.Count} => {string.Join(",", drawnBalls)}");
                }
            }
        }

        Console.WriteLine("Apagando servidor...");
        listener.Stop();
        lock (clientsLock)
        {
            foreach (var c in clients)
            {
                try { SendLine(c.Stream, "GAME_OVER|server_shutdown"); c.Tcp.Close(); } catch { }
            }
        }
    }

    static void Broadcast(List<ClientInfo> clients, string message)
    {
        foreach (var c in clients.ToArray())
        {
            try
            {
                SendLine(c.Stream, message);
            }
            catch
            {
                // ignore send errors here; client read loop handles disconnection
            }
        }
    }

    static bool ValidateBingo(int[,] card, HashSet<string> drawnBalls)
    {
        // build set of drawn numbers as ints
        var drawnInts = new HashSet<int>();
        foreach (var b in drawnBalls)
        {
            // format e.g. B12
            string numStr = b.Substring(1);
            if (int.TryParse(numStr, out int n)) drawnInts.Add(n);
        }
        // check rows
        for (int r = 0; r < 5; r++)
        {
            bool full = true;
            for (int c = 0; c < 5; c++)
            {
                int val = card[r, c];
                if (val == 0) continue; // FREE
                if (!drawnInts.Contains(val)) { full = false; break; }
            }
            if (full) return true;
        }
        // check cols
        for (int c = 0; c < 5; c++)
        {
            bool full = true;
            for (int r = 0; r < 5; r++)
            {
                int val = card[r, c];
                if (val == 0) continue;
                if (!drawnInts.Contains(val)) { full = false; break; }
            }
            if (full) return true;
        }
        return false;
    }

    static List<string> GenerateAllBalls()
    {
        var res = new List<string>();
        string letters = "BINGO";
        for (int i = 0; i < 5; i++)
        {
            int start = i * 15 + 1;
            for (int n = start; n <= start + 14; n++)
            {
                res.Add($"{letters[i]}{n}");
            }
        }
        return res;
    }

    static void SendLine(NetworkStream stream, string line)
    {
        if (!stream.CanWrite) return;
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    static string ReadLineFromStream(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        try
        {
            while (true)
            {
                int read = stream.Read(buffer, 0, 1);
                if (read == 0) return null; // closed
                char ch = (char)buffer[0];
                if (ch == '\n') break;
                if (ch != '\r') sb.Append(ch);
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    static int[,] GenerateCard()
    {
        // card indexed [row, col] rows 0..4, cols 0..4
        var card = new int[5, 5];
        // Column ranges
        var ranges = new (int min, int max)[]
        {
            (1,15), (16,30), (31,45), (46,60), (61,75)
        };
        for (int col = 0; col < 5; col++)
        {
            var pool = Enumerable.Range(ranges[col].min, ranges[col].max - ranges[col].min + 1).ToList();
            Shuffle(pool);
            for (int row = 0; row < 5; row++)
            {
                card[row, col] = pool[row];
            }
        }
        // FREE center
        card[2, 2] = 0;
        return card;
    }

    static string SerializeCard(int[,] card)
    {
        // CSV rows with ; between cells, 0 means FREE
        var parts = new List<string>();
        for (int r = 0; r < 5; r++)
        {
            var row = new List<string>();
            for (int c = 0; c < 5; c++) row.Add(card[r, c].ToString());
            parts.Add(string.Join(",", row));
        }
        return string.Join(";", parts);
    }

    static void Shuffle<T>(IList<T> list)
    {
        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = rng.Next(n + 1);
            T tmp = list[k];
            list[k] = list[n];
            list[n] = tmp;
        }
    }

    #endregion

    #region CLIENT
    static void RunClient(string argName)
    {
        Console.Write("Nombre del jugador: ");
        string name = argName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = $"Jugador{rng.Next(1000, 9999)}";
        }

        TcpClient tcp = new TcpClient();
        try
        {
            tcp.Connect(HOST, PORT);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"No pude conectar al servidor en {HOST}:{PORT} -> {ex.Message}");
            return;
        }

        var stream = tcp.GetStream();
        SendLine(stream, "HELLO|" + name);

        int[,] card = null;
        var marked = new bool[5, 5]; // marked positions
        object markLock = new object();
        bool gameOver = false;

        // Reader thread
        Thread reader = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    string line = ReadLineFromStream(stream);
                    if (line == null) break;
                    if (line.StartsWith("CARD|"))
                    {
                        string serialized = line.Substring("CARD|".Length);
                        card = DeserializeCard(serialized);
                        // mark center free
                        marked[2, 2] = true;
                        Console.Clear();
                        Console.WriteLine($"Cartilla recibida para {name}:");
                        PrintCard(card, marked);
                        Console.WriteLine("Esperando balotas...");
                    }
                    else if (line.StartsWith("BALL|"))
                    {
                        string ball = line.Substring("BALL|".Length).Trim();
                        Console.WriteLine($"\nBalota: {ball}");
                        int num = int.Parse(ball.Substring(1));
                        // mark if number in card
                        bool hit = false;
                        lock (markLock)
                        {
                            for (int r = 0; r < 5; r++)
                                for (int c = 0; c < 5; c++)
                                {
                                    if (card[r, c] == num)
                                    {
                                        marked[r, c] = true;
                                        hit = true;
                                    }
                                }
                        }
                        if (hit) Console.WriteLine("¡Acertaste! Marcado en tu cartilla.");
                        PrintCard(card, marked);
                        // Check for local bingo (row or column)
                        if (CheckLocalBingo(marked))
                        {
                            Console.WriteLine("Crees tener BINGO. Enviando reclamo al servidor...");
                            SendLine(stream, "BINGO_CLAIM|" + name);
                        }
                    }
                    else if (line.StartsWith("BINGO_VALID|"))
                    {
                        string winner = line.Substring("BINGO_VALID|".Length);
                        Console.WriteLine($"\n*** BINGO VALIDO por {winner} ***");
                    }
                    else if (line.StartsWith("BINGO_INVALID|"))
                    {
                        string who = line.Substring("BINGO_INVALID|".Length);
                        Console.WriteLine($"\nBINGO inválido: {who}");
                    }
                    else if (line.StartsWith("GAME_OVER|"))
                    {
                        string who = line.Substring("GAME_OVER|".Length);
                        Console.WriteLine($"\n--- JUEGO TERMINADO. Ganador: {who} ---");
                        gameOver = true;
                        break;
                    }
                    else
                    {
                        Console.WriteLine("[Info del servidor] " + line);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error lector: " + ex.Message);
            }
            finally
            {
                try { tcp.Close(); } catch { }
            }
        })
        { IsBackground = true };
        reader.Start();

        // Main loop: allow user to type "card" to show card, "exit" para salir
        while (!gameOver)
        {
            var cmd = Console.ReadLine();
            if (cmd == null) break;
            if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
            if (cmd.Equals("card", StringComparison.OrdinalIgnoreCase))
            {
                PrintCard(card, marked);
            }
            if (cmd.Equals("bingo", StringComparison.OrdinalIgnoreCase))
            {
                SendLine(stream, "BINGO_CLAIM|" + name);
            }
        }

        Console.WriteLine("Cliente cerrando...");
    }

    static int[,] DeserializeCard(string serialized)
    {
        var rows = serialized.Split(';');
        var card = new int[5, 5];
        for (int r = 0; r < 5; r++)
        {
            var cols = rows[r].Split(',');
            for (int c = 0; c < 5; c++)
            {
                if (int.TryParse(cols[c], out int v)) card[r, c] = v;
                else card[r, c] = 0;
            }
        }
        return card;
    }

    static void PrintCard(int[,] card, bool[,] marked)
    {
        if (card == null) { Console.WriteLine("(Aún no tienes cartilla)"); return; }
        Console.WriteLine("  B   I   N   G   O");
        for (int r = 0; r < 5; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                string cell;
                if (card[r, c] == 0) cell = "FREE";
                else cell = card[r, c].ToString().PadLeft(2, ' ');
                if (marked[r, c]) Console.Write($"[{cell}] ");
                else Console.Write($" {cell}  ");
            }
            Console.WriteLine();
        }
    }

    static bool CheckLocalBingo(bool[,] marked)
    {
        // rows
        for (int r = 0; r < 5; r++)
        {
            bool full = true;
            for (int c = 0; c < 5; c++) if (!marked[r, c]) { full = false; break; }
            if (full) return true;
        }
        // cols
        for (int c = 0; c < 5; c++)
        {
            bool full = true;
            for (int r = 0; r < 5; r++) if (!marked[r, c]) { full = false; break; }
            if (full) return true;
        }
        return false;
    }

    #endregion
}
