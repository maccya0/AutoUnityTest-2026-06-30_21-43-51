using System;
using NUnit.Framework;

public class CalculatorTests
    {
        [Test]
        public void Add_同値分割_正数()
        {
            Assert.AreEqual(10, Calculator.Add(5, 5));
        }

        [Test]
        public void Add_同値分割_負数()
        {
            Assert.AreEqual(-10, Calculator.Add(-5, -5));
        }

        [Test]
        public void Add_同値分割_0()
        {
            Assert.AreEqual(0, Calculator.Add(0, 0));
        }

        [Test]
        public void Add_同値分割_最大値()
        {
            Assert.AreEqual(int.MaxValue, Calculator.Add(int.MaxValue, int.MaxValue));
        }

        [Test]
        public void Add_同値分割_最小値()
        {
            Assert.AreEqual(int.MinValue, Calculator.Add(int.MinValue, int.MinValue));
        }

        [Test]
        public void Add_異常系_負の値()
        {
            Assert.AreEqual(-20, Calculator.Add(-10, -10));
        }

        [Test]
        public void Add_異常系_最大値_プラス_1()
        {
        Assert.AreEqual(0, Calculator.Add(int.MaxValue, 1));
        //Assert.Throws<OverflowException>(() => Calculator.Add(int.MaxValue, 1));
        }

        [Test]
        public void Add_異常系_最小値_マイナス_1()
        {
        Assert.AreEqual(0, Calculator.Add(int.MinValue, -1));
        //Assert.Throws<OverflowException>(() => Calculator.Add(int.MinValue, -1));
        }
    }