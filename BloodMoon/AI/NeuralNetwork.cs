using System;
using System.IO;
using UnityEngine;
using Random = UnityEngine.Random;

namespace BloodMoon.AI
{
    /// <summary>
    /// 神经网络类，用于AI决策
    /// </summary>
    [Serializable]
    public class NeuralNetwork
    {
        public int[] Layers;
        public float[][][] Weights;
        public float[][] Biases;
        public float[][] Neurons;

        /// <summary>
        /// 构造函数，初始化神经网络
        /// </summary>
        /// <param name="layers">各层的大小数组</param>
        public NeuralNetwork(int[] layers)
        {
            Layers = new int[layers.Length];
            Array.Copy(layers, Layers, layers.Length);
            
            Neurons = new float[layers.Length][];
            Weights = new float[layers.Length][][];
            Biases = new float[layers.Length][];

            for (int i = 0; i < layers.Length; i++)
            {
                Neurons[i] = new float[layers[i]];
                
                if (i > 0)
                {
                    int prevLayerSize = layers[i - 1];
                    Weights[i] = new float[layers[i]][];
                    Biases[i] = new float[layers[i]];
                    
                    for (int j = 0; j < layers[i]; j++)
                    {
                        Weights[i][j] = new float[prevLayerSize];
                    }
                }
            }
        }

        /// <summary>
        /// 随机初始化网络权重和偏置
        /// </summary>
        public void InitializeRandom()
        {
            if (Weights == null || Biases == null)
            {
                return;
            }

            for (int i = 0; i < Weights.Length; i++)
            {
                if (Weights[i] == null) continue;
                for (int j = 0; j < Weights[i].Length; j++)
                {
                    if (Weights[i][j] == null) continue;
                    for (int k = 0; k < Weights[i][j].Length; k++)
                    {
                        Weights[i][j][k] = Random.Range(-0.5f, 0.5f);
                    }
                }
            }
            
            for (int i = 1; i < Biases.Length; i++)
            {
                if (Biases[i] == null) continue;
                for (int j = 0; j < Biases[i].Length; j++)
                {
                    Biases[i][j] = Random.Range(-0.1f, 0.1f);
                }
            }
        }

        /// <summary>
        /// 前向传播，计算网络输出
        /// </summary>
        /// <param name="inputs">输入数据</param>
        /// <returns>网络输出</returns>
        public float[] FeedForward(float[] inputs)
        {
            int inputCount = Math.Min(inputs.Length, Neurons[0].Length);
            for (int i = 0; i < inputCount; i++)
            {
                Neurons[0][i] = inputs[i];
            }

            for (int i = 0; i < Layers.Length - 1; i++)
            {
                int currentLayerIdx = i;
                int nextLayerIdx = i + 1;

                if (Weights == null || Weights[nextLayerIdx] == null || Biases == null || Biases[nextLayerIdx] == null)
                {
                    continue;
                }

                for (int nextNode = 0; nextNode < Layers[nextLayerIdx]; nextNode++)
                {
                    float value = 0f;

                    if (Weights[nextLayerIdx][nextNode] == null)
                    {
                        continue;
                    }

                    for (int currentNode = 0; currentNode < Layers[currentLayerIdx]; currentNode++)
                    {
                        if (currentNode < Weights[nextLayerIdx][nextNode].Length)
                        {
                            value += Neurons[currentLayerIdx][currentNode] * Weights[nextLayerIdx][nextNode][currentNode];
                        }
                    }

                    if (nextNode < Biases[nextLayerIdx].Length)
                    {
                        value += Biases[nextLayerIdx][nextNode];
                    }

                    if (nextNode < Neurons[nextLayerIdx].Length)
                    {
                        if (nextLayerIdx == Layers.Length - 1)
                        {
                            Neurons[nextLayerIdx][nextNode] = Sigmoid(value);
                        }
                        else
                        {
                            Neurons[nextLayerIdx][nextNode] = (float)Math.Tanh(value);
                        }
                    }
                }
            }

            return Neurons[Layers.Length - 1];
        }

        /// <summary>
        /// Sigmoid激活函数
        /// </summary>
        /// <param name="value">输入值</param>
        /// <returns>激活后的值</returns>
        private float Sigmoid(float value)
        {
            return 1.0f / (1.0f + (float)Math.Exp(-value));
        }

        /// <summary>
        /// 变异操作，随机修改权重和偏置
        /// </summary>
        /// <param name="mutationRate">变异率</param>
        /// <param name="mutationStrength">变异强度</param>
        public void Mutate(float mutationRate, float mutationStrength)
        {
            for (int i = 0; i < Weights.Length; i++)
            {
                for (int j = 0; j < Weights[i].Length; j++)
                {
                    for (int k = 0; k < Weights[i][j].Length; k++)
                    {
                        if (Random.value < mutationRate)
                        {
                            Weights[i][j][k] += Random.Range(-mutationStrength, mutationStrength);
                        }
                    }
                }
            }
            
            for (int i = 1; i < Biases.Length; i++)
            {
                for (int j = 0; j < Biases[i].Length; j++)
                {
                    if (Random.value < mutationRate)
                    {
                        Biases[i][j] += Random.Range(-mutationStrength, mutationStrength);
                    }
                }
            }
        }
        
        /// <summary>
        /// 将神经网络保存为JSON字符串
        /// </summary>
        /// <returns>JSON格式的字符串</returns>
        public string SaveToString()
        {
            return JsonUtility.ToJson(this);
        }

        /// <summary>
        /// 从JSON字符串加载神经网络
        /// </summary>
        /// <param name="json">JSON格式的字符串</param>
        /// <returns>加载的神经网络</returns>
        public static NeuralNetwork LoadFromString(string json)
        {
            return JsonUtility.FromJson<NeuralNetwork>(json);
        }
    }
}
