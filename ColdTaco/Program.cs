using System;
using System.Collections.Generic;
using Nintaco;
using ColdClearSharp;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
using System.Diagnostics;

namespace ColdTaco
{
    class Addresses
    {
        public const int COPYRIGHT_UNSKIPPABLE = 0x00C3;
        public const int TETRIMINO_TYPE_TABLE = 0x993B;
        public const int CURRENT_PIECE_ID = 0x0042;
        public const int NEXT_PIECE_ID = 0x00BF;
        public const int GAME_STATE = 0x0048;
    }
    class Program
    {
        static readonly IEnumerator<int> player = Play().GetEnumerator();
        static int framesToWait = 0;
        static bool softDrop = false;
        static readonly CCPiece[] tetriminoMap = new CCPiece[19];
        static readonly Queue<int> inputs = new Queue<int>();
        static int currentInput = -1;
        static void Main(string[] args) {
            ApiSource.initRemoteAPI("localhost", args.Length > 1 ? int.Parse(args[1]) : 9999);
            ApiSource.API.addFrameListener(RenderFinished);
            ApiSource.API.addStatusListener(StatusChanged);
            ApiSource.API.addActivateListener(ApiEnabled);
            ApiSource.API.addDeactivateListener(ApiDisabled);
            ApiSource.API.addStopListener(Dispose);
            ApiSource.API.run();
        }
        static IEnumerable<int> Play() {
            for (int i = 0; i < tetriminoMap.Length; i++) {
                tetriminoMap[i] = ApiSource.API.peekCPU(Addresses.TETRIMINO_TYPE_TABLE + i) switch {
                    0 => CCPiece.CcT,
                    1 => CCPiece.CcJ,
                    2 => CCPiece.CcZ,
                    3 => CCPiece.CcO,
                    4 => CCPiece.CcS,
                    5 => CCPiece.CcL,
                    6 => CCPiece.CcI,
                    _ => throw new InvalidOperationException("Invalid piece type!")
                };
            }
            ColdClear.CcDefaultOptions(out CCOptions options);
            options.Mode = CCMovementMode.CcHardDropOnly;
            options.UseHold = false;
            options.Pcloop = false;
            options.Speculate = true;
            ColdClear.CcDefaultWeights(out CCWeights weights);
            weights._Tslot = new int[] {
                -1000,
                -1000,
                -1000,
                -1000
            };
            weights.Jeopardy = -20;
            weights.TopHalf = -511;
            weights.ComboGarbage = 0;
            weights.CavityCells = -173 * 2;
            weights.CavityCellsSq = -3;
            weights.OverhangCells = -173 * 2;
            weights.OverhangCellsSq = -3;
            weights.Clear1 = -71;
            weights.Clear2 = -50;
            weights.Clear3 = -29;
            weights.WellColumn[0] = 100;
            weights.WellColumn[9] = 100;
            weights.Bumpiness = -14;
            weights.BumpinessSq = -12;
            IntPtr bot = ColdClear.CcLaunchAsync(ref options, ref weights);
            while (ApiSource.API.peekCPU(Addresses.GAME_STATE) == 0) {
                yield return 1;
            }
            ApiSource.API.addControllersListener(InputPolled);
            int prevGameState = 0;
            int framesSinceSpawn = -1;
            while (true) {
                int gameState = ApiSource.API.peekCPU(Addresses.GAME_STATE);
                if ((prevGameState == 8 || prevGameState == 0) && gameState == 1) {
                    if (prevGameState == 0) {
                        ColdClear.CcAddNextPieceAsync(bot, tetriminoMap[ApiSource.API.peekCPU(Addresses.CURRENT_PIECE_ID)]);
                    }
                    ColdClear.CcAddNextPieceAsync(bot, tetriminoMap[ApiSource.API.peekCPU(Addresses.NEXT_PIECE_ID)]);
                    framesSinceSpawn = 0;
                    softDrop = false;
                }
                if (++framesSinceSpawn == 2) {
                    CCPiece current = tetriminoMap[ApiSource.API.peekCPU(Addresses.CURRENT_PIECE_ID)];
                    ColdClear.CcRequestNextMove(bot, 0);
                    ColdClear.CcBlockNextMove(bot, out CCMove move, out _, 0);
                    int movements = 0;
                    int rotation = 0;
                    foreach (CCMovement movement in move.Movements.Take(move.MovementCount)) {
                        if (current == CCPiece.CcI || current == CCPiece.CcS || current == CCPiece.CcZ) {
                            if (movement == CCMovement.CcCcw) {
                                switch (rotation) {
                                    case 0:
                                        --movements;
                                        break;
                                    case 3:
                                        ++movements;
                                        break;
                                }
                                
                            }
                            if (movement == CCMovement.CcCw) {
                                switch (rotation) {
                                    case 2:
                                        --movements;
                                        break;
                                    case 3:
                                        ++movements;
                                        break;
                                }
                            }
                        }
                        switch (movement) {
                            case CCMovement.CcCcw:
                                rotation = rotation > 0 ? (rotation - 1) : 3;
                                break;
                            case CCMovement.CcCw:
                                rotation = rotation < 3 ? (rotation + 1) : 0;
                                break;
                            case CCMovement.CcLeft:
                                --movements;
                                break;
                            case CCMovement.CcRight:
                                ++movements;
                                break;
                        }
                    }
                    if (current != CCPiece.CcI && current != CCPiece.CcO) {
                        rotation = (rotation + 2) % 4;
                        --movements;
                    }
                    switch (rotation) {
                        case 1:
                            inputs.Enqueue(GamepadButtons.A);
                            break;
                        case 2:
                            inputs.Enqueue(GamepadButtons.A);
                            inputs.Enqueue(GamepadButtons.A);
                            break;
                        case 3:
                            inputs.Enqueue(GamepadButtons.B);
                            break;
                        default:
                            break;
                    }
                    while (movements != 0) {
                        if (movements > 0) {
                            inputs.Enqueue(GamepadButtons.Right);
                            --movements;
                        } else {
                            inputs.Enqueue(GamepadButtons.Left);
                            ++movements;
                        }
                    }
                    yield return 1;
                    softDrop = true;
                }
                prevGameState = gameState;
                yield return 1;
            }
        }
        static void InputPolled() {
            if (currentInput == -1) {
                if (inputs.TryDequeue(out currentInput)) {
                    ApiSource.API.writeGamepad(0, currentInput, true);
                } else {
                    ApiSource.API.writeGamepad(0, GamepadButtons.Down, softDrop);
                    currentInput = -1;
                }
            } else {
                ApiSource.API.writeGamepad(0, currentInput, false);
                currentInput = -1;
            }
        }
        static void RenderFinished() {
            if (--framesToWait < 0 && player.MoveNext()) {
                framesToWait = player.Current;
            }
        }
        static void StatusChanged(string message) {
            Console.WriteLine("Status message: {0}", message);
        }
        static void ApiEnabled() {
            Console.WriteLine("API enabled");
            ApiSource.API.reset();
        }
        static void ApiDisabled() {
            Console.WriteLine("API disabled");
        }
        static void Dispose() {
            Console.WriteLine("API stopped");
        }
    }
}
